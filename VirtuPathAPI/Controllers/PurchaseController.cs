using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/purchase")]
    [Authorize] // must be logged in (same cookie as /users/me)
    public class PurchaseController : ControllerBase
    {
        private readonly UserContext _users;// Tiny DTO for status view
        private sealed record SubByTxnDto(int SubscriptionID, int UserID, int CareerPathID, string? PaddlePriceId);

        private readonly UserSubscriptionContext _subs;
        private readonly ILogger<PurchaseController> _log;
        private readonly string? _paddleApiKey;

        public PurchaseController(
            UserContext users,
            UserSubscriptionContext subs,
            ILogger<PurchaseController> log,
            IConfiguration cfg)
        {
            _users = users;
            _subs  = subs;
            _log   = log;
            _paddleApiKey = cfg["Paddle:ApiKey"];
        }

        // ─────────────────────────────────────────────────────────
        // Models for Paddle fetch (just what we use)
        private sealed record TxnResponse(TxnData data);
        private sealed record TxnData(
            string id,
            string status,
            Details? details,
            string? customer_email,
            Customer? customer,
            JsonElement? custom_data
        );
        private sealed record Details(List<LineItem>? line_items);
        private sealed record LineItem(string id, string price_id, int quantity);
        private sealed record Customer(string? email);

        public sealed record ConfirmReq(string? ptxn); // body/qs

        // ─────────────────────────────────────────────────────────
        // GET /api/purchase/status?ptxn=...  → what FE needs to know
       [HttpGet("status")]
        public async Task<IActionResult> Status([FromQuery] string? ptxn = null)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId is null) return Unauthorized(new { error = "not_authed" });

            var me = await _users.Users
                .Where(u => u.UserID == userId.Value)
                .Select(u => new { u.UserID, u.Email, u.CareerPathID, u.CurrentDay })
                .FirstOrDefaultAsync();

            var subsMine = await _subs.UserSubscriptions
                .Where(s => s.UserID == userId.Value)
                .OrderByDescending(s => s.SubscriptionID)
                .Take(10)
                .Select(s => new {
                    s.SubscriptionID, s.UserID, s.CareerPathID, s.PlanName, s.Billing,
                    s.PaddleTransactionId, s.PaddlePriceId, s.StartDate, s.EndDate
                })
                .ToListAsync();

            // ↓↓ FIXED: concrete DTO, not object[]
            var byTxn = new List<SubByTxnDto>();
            if (!string.IsNullOrWhiteSpace(ptxn))
            {
                byTxn = await _subs.UserSubscriptions
                    .Where(s => s.PaddleTransactionId == ptxn)
                    .Select(s => new SubByTxnDto(
                        s.SubscriptionID,
                        s.UserID,
                        s.CareerPathID,
                        s.PaddlePriceId
                    ))
                    .ToListAsync();
            }

            return Ok(new {
                authed = true,
                me,
                subsMine,
                byTxn,
                note = "If me.CurrentDay > 0 the FE should show 'unlocked'."
            });
        }

        // ─────────────────────────────────────────────────────────
        // POST /api/purchase/confirm  { ptxn?: "..." }
        // If ptxn missing, we try to locate the latest completed txn for this customer.
        [HttpPost("confirm")]
        public async Task<IActionResult> Confirm([FromBody] ConfirmReq body)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId is null) return Unauthorized(new { error = "not_authed" });

            if (string.IsNullOrWhiteSpace(_paddleApiKey))
                return StatusCode(500, new { error = "paddle_api_key_missing" });

            // 1) Who is the user?
            var user = await _users.Users.FirstOrDefaultAsync(u => u.UserID == userId.Value);
            if (user is null) return Unauthorized(new { error = "user_not_found" });
            var email = user.Email?.Trim().ToLowerInvariant();

            // 2) Determine the transaction id
            string? txnId = body?.ptxn;

            if (string.IsNullOrWhiteSpace(txnId))
            {
                // No ptxn provided → try to locate latest completed txn for this customer via Paddle
                // (search customer by email → list transactions)
                var custId = await FindCustomerIdByEmailAsync(email);
                if (custId == null)
                    return BadRequest(new { error = "no_ptxn_and_cannot_find_customer" });

                txnId = await FindLatestCompletedTxnForCustomerAsync(custId);
                if (txnId == null)
                    return BadRequest(new { error = "no_completed_txn_for_customer" });
            }

            // 3) Fetch the transaction details
            var txn = await FetchTxnAsync(txnId!);
            if (txn == null)
                return BadRequest(new { error = "paddle_txn_not_found_or_failed" });

            if (!string.Equals(txn.status, "completed", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "txn_not_completed_yet", txn = txn.id, status = txn.status });

            // 4) Extract price_ids purchased
            var purchased = (txn.details?.line_items ?? new List<LineItem>())
                .Where(li => !string.IsNullOrWhiteSpace(li.price_id))
                .Select(li => (priceId: li.price_id, qty: Math.Max(1, li.quantity)))
                .ToList();

            if (purchased.Count == 0)
                return BadRequest(new { error = "no_price_ids_in_txn" });

            var priceIds = purchased.Select(p => p.priceId).ToList();

            // 5) Map price_ids → (CareerPathID, Plan, Billing)
            var maps = await _subs.PriceMaps
                .Where(pm => pm.Active && priceIds.Contains(pm.PaddlePriceId))
                .ToListAsync();

            var missing = priceIds.Where(pid => !maps.Any(m => m.PaddlePriceId.Equals(pid, StringComparison.OrdinalIgnoreCase)))
                                  .ToList();
            if (missing.Count > 0)
            {
                return BadRequest(new {
                    error = "missing_pricemaps",
                    missing,
                    hint = "Insert rows into dbo.PriceMaps for these price_ids."
                });
            }

            // 6) Idempotent provisioning for *this* user only
            var created = new List<object>();
            foreach (var pid in priceIds)
            {
                var map = maps.First(m => m.PaddlePriceId.Equals(pid, StringComparison.OrdinalIgnoreCase));

                var exists = await _subs.UserSubscriptions.AnyAsync(s =>
                    s.UserID == user.UserID &&
                    s.CareerPathID == map.CareerPathID &&
                    s.PaddleTransactionId == txn.id);

                if (!exists)
                {
                    var sub = new UserSubscription
                    {
                        UserID              = user.UserID,
                        CareerPathID        = map.CareerPathID,
                        StartDate           = DateTime.UtcNow,
                        LastAccessedDay     = 0,
                        PaddleTransactionId = txn.id,
                        PaddlePriceId       = pid,
                        PlanName            = map.PlanName,
                        Billing             = map.Billing
                    };
                    _subs.UserSubscriptions.Add(sub);
                    await _subs.SaveChangesAsync();
                    created.Add(new { sub.SubscriptionID, sub.CareerPathID, sub.PaddlePriceId });
                }
            }

            // 7) Ensure user is unlocked
            if (user.CurrentDay <= 0) user.CurrentDay = 1;
            // (if you want first purchased career to become "active" view)
            var firstCareer = maps.FirstOrDefault()?.CareerPathID;
            if (firstCareer.HasValue) user.CareerPathID = firstCareer.Value;
            user.LastActiveAt = DateTime.UtcNow;
            await _users.SaveChangesAsync();

            return Ok(new {
                ok = true,
                user = new { user.UserID, user.CareerPathID, user.CurrentDay },
                txn = txn.id,
                created
            });
        }

        // ─────────────────────────── helpers ───────────────────────────

        private async Task<TxnData?> FetchTxnAsync(string txnId)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://api.paddle.com/") };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _paddleApiKey);

                var resp = await http.GetAsync($"transactions/{txnId}");
                if (!resp.IsSuccessStatusCode) return null;

                using var s = await resp.Content.ReadAsStreamAsync();
                var doc = await JsonDocument.ParseAsync(s);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    return JsonSerializer.Deserialize<TxnData>(data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FetchTxnAsync failed for {Txn}", txnId);
            }
            return null;
        }

        private async Task<string?> FindCustomerIdByEmailAsync(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://api.paddle.com/") };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _paddleApiKey);

                // Search customers by email
                var resp = await http.GetAsync($"customers?search={Uri.EscapeDataString(email)}&per_page=1");
                if (!resp.IsSuccessStatusCode) return null;

                using var s = await resp.Content.ReadAsStreamAsync();
                var doc = await JsonDocument.ParseAsync(s);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array &&
                    data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    if (first.TryGetProperty("id", out var idProp))
                        return idProp.GetString();
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FindCustomerIdByEmailAsync failed for {Email}", email);
            }
            return null;
        }

        private async Task<string?> FindLatestCompletedTxnForCustomerAsync(string customerId)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://api.paddle.com/") };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _paddleApiKey);

                // List transactions for this customer (most recent first)
                var resp = await http.GetAsync($"transactions?customer_id={Uri.EscapeDataString(customerId)}&per_page=5&order=desc");
                if (!resp.IsSuccessStatusCode) return null;

                using var s = await resp.Content.ReadAsStreamAsync();
                var doc = await JsonDocument.ParseAsync(s);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var item in data.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var status = item.GetProperty("status").GetString();
                    if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FindLatestCompletedTxnForCustomerAsync failed for {Cust}", customerId);
            }
            return null;
        }
    }
}
