using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/paddle/webhook")]
    public class PaddleWebhookController : ControllerBase
    {
        private readonly UserContext _users;
        private readonly UserSubscriptionContext _subs;
        private readonly ILogger<PaddleWebhookController> _log;
        private readonly string _secret;
        private readonly string? _paddleApiKey; // used for confirm/return fetch

        public PaddleWebhookController(
            UserContext users,
            UserSubscriptionContext subs,
            ILogger<PaddleWebhookController> log,
            IConfiguration cfg)
        {
            _users = users;
            _subs  = subs;
            _log   = log;

            _secret = cfg["Paddle:WebhookSecret"] ?? throw new InvalidOperationException("Missing Paddle:WebhookSecret");
            _paddleApiKey = cfg["Paddle:ApiKey"]; // must match sandbox/live environment you’re using
        }

        // ---------- Payload models ----------
        private sealed record PaddleEvent(string event_id, string event_type, DateTime occurred_at, Transaction? data);

        private sealed record Transaction(
            string id,
            string status,
            JsonElement? custom_data,
            List<TxnItem>? items,
            string? customer_email,
            Customer? customer,
            Details? details,
            string? customer_id
        );
        private sealed record Customer(string? email);
        private sealed record TxnItem(TxnPrice price, int quantity);
        private sealed record TxnPrice(string id);
        private sealed record Details(List<LineItem>? line_items);
        private sealed record LineItem(string id, string price_id, int quantity);

        // custom_data we send from the client
        private sealed record CustomData(int? userId, string? email, List<CustomItem>? items);
        private sealed record CustomItem(int careerPathID, string plan, string billing, int quantity);

        // -------- ping / options ----------
        [HttpGet, AllowAnonymous]
        public IActionResult GetPing() => Ok(new { ok = true, route = "api/paddle/webhook" });

        [HttpHead, AllowAnonymous] public IActionResult Head() => Ok();

        [HttpOptions, AllowAnonymous]
        public IActionResult Options()
        {
            Response.Headers["Allow"] = "OPTIONS, GET, HEAD, POST";
            return NoContent();
        }

        // --------------- Paddle webhook (HMAC verified) -------------------
        [HttpPost, AllowAnonymous]
        [Consumes("application/json")]
        public async Task<IActionResult> Receive()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
                raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var sigHeader = Request.Headers["Paddle-Signature"].ToString();
            if (!VerifyPaddleSignature(sigHeader, raw, _secret))
            {
                _log.LogWarning("Paddle webhook: invalid signature");
                return Unauthorized();
            }

            PaddleEvent? ev;
            try
            {
                ev = JsonSerializer.Deserialize<PaddleEvent>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Paddle webhook: failed to deserialize payload");
                return BadRequest();
            }

            if (ev?.data is null)
            {
                _log.LogWarning("Paddle webhook: missing event data");
                return BadRequest();
            }

            if (!string.Equals(ev.event_type, "transaction.completed", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ev.data.status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Paddle webhook: ignoring event {Type} with status {Status}", ev.event_type, ev.data.status);
                return Ok();
            }

            await ProcessTransactionAsync(ev.data);
            return Ok(new { ok = true });
        }

        // ---------------- Return endpoint (best UX): Paddle redirects here with _ptxn ----------------
    [HttpGet("return"), AllowAnonymous]
    public async Task<IActionResult> Return(
        [FromQuery(Name = "_ptxn")] string? txnId,
        [FromQuery] string? next)
    {
        // If Paddle/SDK didn't give us a transaction id, don't 400 — bounce to thank-you as pending
        if (string.IsNullOrWhiteSpace(txnId))
        {
            _log.LogInformation("Return: no _ptxn in query; redirecting as pending.");
            return Redirect(ComposeNext(next, ok: false, msg: "no_ptxn"));
        }

        var tx = await FetchTransactionFromPaddleAsync(txnId);
        if (tx == null)
        {
            _log.LogWarning("Return: fetch failed for txn {Txn}", txnId);
            return Redirect(ComposeNext(next, ok: false, msg: "fetch_failed"));
        }

        if (!string.Equals(tx.status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("Return: txn {Txn} not completed (status={Status})", txnId, tx.status);
            return Redirect(ComposeNext(next, ok: false, msg: "pending"));
        }

        await ProcessTransactionAsync(tx);
        return Redirect(ComposeNext(next, ok: true, msg: null));
    }

        private static string ComposeNext(string? next, bool ok, string? msg)
        {
            var dest = string.IsNullOrWhiteSpace(next) ? "/thank-you" : next!;
            var sep = dest.Contains('?') ? "&" : "?";
            var q = ok ? "ok=1" : "ok=0";
            if (!string.IsNullOrWhiteSpace(msg)) q += "&reason=" + Uri.EscapeDataString(msg);
            return dest + sep + q;
        }

        // ---------------- Confirm endpoint (optional JSON path from client) ----------------
        public sealed class ConfirmReq { public string? TxnId { get; set; } } // must be public to avoid CS0051

        [HttpPost("confirm"), AllowAnonymous]
        public async Task<IActionResult> Confirm([FromBody] ConfirmReq req)
        {
            if (string.IsNullOrWhiteSpace(req?.TxnId))
                return BadRequest(new { error = "Missing txnId" });

            var tx = await FetchTransactionFromPaddleAsync(req.TxnId);
            if (tx == null)
                return BadRequest(new { error = "Could not fetch transaction from Paddle" });

            if (!string.Equals(tx.status, "completed", StringComparison.OrdinalIgnoreCase))
                return Ok(new { ok = false, status = tx.status });

            await ProcessTransactionAsync(tx);
            return Ok(new { ok = true });
        }

        // ---------------- Core unlock logic (used by webhook, return, confirm) ----------------
        private async Task ProcessTransactionAsync(Transaction tx)
        {
            // Prefer custom_data from payload if present
            CustomData? cd = null;
            if (tx.custom_data is JsonElement ce && ce.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    cd = JsonSerializer.Deserialize<CustomData>(ce.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "custom_data parse failed (txn={Txn})", tx.id);
                }
            }

            var itemsToProvision = await BuildProvisioningItemsAsync(cd, tx);
            if (itemsToProvision.Count == 0)
            {
                _log.LogWarning("No items to provision for txn {Txn}", tx.id);
                return;
            }

            var resolvedUserId = await ResolveUserIdAsync(cd, tx);
            if (resolvedUserId == null)
            {
                _log.LogWarning("Could not resolve user for txn {Txn}", tx.id);
                return;
            }

            foreach (var it in itemsToProvision)
            {
                var priceId  = it.PriceId;
                var careerId = it.Item.careerPathID;
                var plan     = it.Item.plan;
                var billing  = it.Item.billing;

                var exists = await _subs.UserSubscriptions.AnyAsync(s =>
                    s.UserID == resolvedUserId &&
                    s.CareerPathID == careerId &&
                    s.LastTransactionId == tx.id);

                if (!exists)
                {
                    var startUtc = DateTime.UtcNow;
                     DateTime? periodEnd = null;
                     if (string.Equals(billing, "monthly", StringComparison.OrdinalIgnoreCase))
                         periodEnd = startUtc.AddDays(30);
                     else if (string.Equals(billing, "yearly", StringComparison.OrdinalIgnoreCase))
                         periodEnd = startUtc.AddDays(365);
                     // else: one_time / lifetime => leave null
                    
                     var sub = new UserSubscription
                     {
                         UserID           = resolvedUserId.Value,
                         CareerPathID     = careerId,
                         Plan             = plan,      // "starter" | "pro" | "bonus"
                         Billing          = billing,   // "monthly" | "yearly" | "one_time"
                         StartAt          = startUtc,
                         CurrentPeriodEnd = periodEnd,
                         LastTransactionId= tx.id,
                         IsActive         = true,
                         IsCanceled       = false,
                     };
                    _subs.UserSubscriptions.Add(sub);
                    await _subs.SaveChangesAsync();

                    _log.LogInformation("Provisioned sub (user={UserId}, career={CareerId}, txn={Txn}, price={PriceId}, plan={Plan}, billing={Billing})",
                        resolvedUserId, careerId, tx.id, priceId, plan, billing);
                }
                else
                {
                    _log.LogInformation("Sub already exists (user={UserId}, career={CareerId}, txn={Txn})",
                        resolvedUserId, careerId, tx.id);
                }

                // unlock user
                var user = await _users.Users.FirstOrDefaultAsync(u => u.UserID == resolvedUserId);
                if (user != null)
                {
                    user.CareerPathID = careerId;
                    if (user.CurrentDay <= 0) user.CurrentDay = 1;
                    user.LastTaskDate = DateTime.UtcNow;
                    user.LastActiveAt = DateTime.UtcNow;
                    await _users.SaveChangesAsync();
                }
            }
        }

        // ---------- Build items to provision ----------
        private sealed record PriceMappedItem(CustomItem Item, string? PriceId);

        private async Task<List<PriceMappedItem>> BuildProvisioningItemsAsync(CustomData? cd, Transaction data)
        {
            // Use custom_data if provided
            if (cd?.items is { Count: > 0 })
            {
                var payloadPriceIds = CollectPriceIds(data);
                var mapped = new List<PriceMappedItem>();
                string? maybePriceId = payloadPriceIds.Count > 0 ? payloadPriceIds[0].priceId : null;

                foreach (var item in cd.items)
                    mapped.Add(new PriceMappedItem(item, maybePriceId));

                return mapped;
            }

            // else derive from price map table
            var derived  = new List<PriceMappedItem>();
            var purchased = CollectPriceIds(data);
            if (purchased.Count == 0) return derived;

            var ids = purchased.Select(p => p.priceId).ToList();

            var maps = await _subs.PriceMaps
                .Where(pm => pm.Active && ids.Contains(pm.PaddlePriceId))
                .ToListAsync();

            foreach (var (pid, qty) in purchased)
            {
                var map = maps.FirstOrDefault(m => string.Equals(m.PaddlePriceId, pid, StringComparison.OrdinalIgnoreCase));
                if (map == null)
                {
                    _log.LogWarning("No mapping for price_id {Pid}", pid);
                    continue;
                }

                derived.Add(new PriceMappedItem(
                    new CustomItem(map.CareerPathID, map.PlanName, map.Billing, Math.Max(1, qty)),
                    pid));
            }

            return derived;
        }

        private List<(string priceId, int qty)> CollectPriceIds(Transaction data)
        {
            var list = new List<(string, int)>();

            if (data.details?.line_items is { Count: > 0 })
            {
                foreach (var li in data.details.line_items)
                    if (!string.IsNullOrWhiteSpace(li.price_id))
                        list.Add((li.price_id, Math.Max(1, li.quantity)));
            }
            else if (data.items is { Count: > 0 })
            {
                foreach (var it in data.items)
                    if (!string.IsNullOrWhiteSpace(it.price?.id))
                        list.Add((it.price.id, Math.Max(1, it.quantity)));
            }

            return list
                .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Sum(x => x.Item2)))
                .ToList();
        }

        // ---------- User resolution ----------
        private async Task<int?> ResolveUserIdAsync(CustomData? cd, Transaction data)
        {
            if (cd?.userId is int uid && uid > 0) return uid;

            var email =
                cd?.email?.Trim() ??
                data.customer_email?.Trim() ??
                data.customer?.email?.Trim() ??
                await TryFetchCustomerEmailAsync(data.customer_id);

            if (string.IsNullOrWhiteSpace(email)) return null;

            email = email.ToLowerInvariant();
            var user = await _users.Users
                .Where(u => u.Email.ToLower() == email)
                .Select(u => new { u.UserID })
                .FirstOrDefaultAsync();

            return user?.UserID;
        }

        // ---------- Paddle API helpers ----------
        private async Task<Transaction?> FetchTransactionFromPaddleAsync(string txnId)
        {
            if (string.IsNullOrWhiteSpace(_paddleApiKey))
            {
                _log.LogError("Paddle: ApiKey missing; cannot fetch transactions.");
                return null;
            }

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://api.paddle.com/") };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _paddleApiKey);

                var resp = await http.GetAsync($"transactions/{txnId}");
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("Paddle API: GET transactions/{Id} failed: {Code}", txnId, (int)resp.StatusCode);
                    return null;
                }

                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);

                // shape: { "data": { ...transaction... } }
                if (!doc.RootElement.TryGetProperty("data", out var dataEl))
                    return null;

                var tx = JsonSerializer.Deserialize<Transaction>(dataEl.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return tx;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Paddle API: error fetching transaction {Id}", txnId);
                return null;
            }
        }

        private async Task<string?> TryFetchCustomerEmailAsync(string? customerId)
        {
            if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(_paddleApiKey))
                return null;

            try
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://api.paddle.com/") };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _paddleApiKey);

                var resp = await http.GetAsync($"customers/{customerId}");
                if (!resp.IsSuccessStatusCode) return null;

                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("email", out var emailProp) &&
                    emailProp.ValueKind == JsonValueKind.String)
                {
                    return emailProp.GetString();
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Paddle API: error resolving customer {Id}", customerId);
            }
            return null;
        }

        // ---------- Signature verification ----------
        private static bool VerifyPaddleSignature(string header, string rawBody, string secret)
        {
            if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret))
                return false;

            string? ts = null, h1 = null;
            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "ts") ts = kv[1];
                if (kv[0] == "h1") h1 = kv[1];
            }
            if (ts == null || h1 == null) return false;

            var payload = $"{ts}:{rawBody}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var hex  = Convert.ToHexString(hash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hex),
                Encoding.ASCII.GetBytes(h1));
        }
    }
}
