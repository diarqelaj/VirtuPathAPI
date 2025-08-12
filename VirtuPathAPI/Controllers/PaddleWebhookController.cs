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
        private readonly string? _paddleApiKey; // optional for customer email fallback

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
            _paddleApiKey = cfg["Paddle:ApiKey"]; // optional, only used if set
        }

        // ---------- Payload models (aligned with your sample) ----------
        private sealed record PaddleEvent(string event_id, string event_type, DateTime occurred_at, Transaction? data);

        private sealed record Transaction(
            string id,
            string status,
            JsonElement? custom_data,
            List<TxnItem>? items,
            string? customer_email,        // sometimes present
            Customer? customer,            // sometimes present (email)
            Details? details,              // contains line_items with price_id
            string? customer_id            // useful for API fallback
        );

        private sealed record Customer(string? email);
        private sealed record TxnItem(TxnPrice price, int quantity);
        private sealed record TxnPrice(string id);
        private sealed record Details(List<LineItem>? line_items);
        private sealed record LineItem(string id, string price_id, int quantity);

        // Custom data from client (if you pass it)
        private sealed record CustomData(int? userId, string? email, List<CustomItem>? items);
        private sealed record CustomItem(int careerPathID, string plan, string billing, int quantity);

        // --------- Verb handlers to avoid 405s ----------
        [HttpGet, AllowAnonymous] public IActionResult GetPing() => Ok(new { ok = true, route = "api/paddle/webhook" });
        [HttpHead, AllowAnonymous] public IActionResult Head() => Ok();
        [HttpOptions, AllowAnonymous]
        public IActionResult Options()
        {
            Response.Headers["Allow"] = "OPTIONS, GET, HEAD, POST";
            return NoContent();
        }

        // --------------- Main webhook -------------------
        [HttpPost]
        [AllowAnonymous]
        [Consumes("application/json")]
        public async Task<IActionResult> Receive()
        {
            // 1) Read raw body (for signature verification)
            Request.EnableBuffering();
            string raw;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
                raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            // 2) Verify HMAC signature
            var sigHeader = Request.Headers["Paddle-Signature"].ToString();
            if (!VerifyPaddleSignature(sigHeader, raw, _secret))
            {
                _log.LogWarning("Paddle webhook: invalid signature");
                return Unauthorized();
            }

            // 3) Deserialize payload
            PaddleEvent? ev;
            try
            {
                ev = JsonSerializer.Deserialize<PaddleEvent>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
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

            // 4) Only act on completed transactions
            if (!string.Equals(ev.event_type, "transaction.completed", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ev.data.status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Paddle webhook: ignoring event {Type} with status {Status}", ev.event_type, ev.data.status);
                return Ok();
            }

            var txnId = ev.data.id;

            // 5) Parse custom_data (if provided)
            CustomData? cd = null;
            if (ev.data.custom_data is JsonElement ce && ce.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    cd = JsonSerializer.Deserialize<CustomData>(ce.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Paddle webhook: custom_data parse failed");
                }
            }

            // 6) Build items to provision (custom_data preferred; else map price_id via DB)
            var itemsToProvision = await BuildProvisioningItemsAsync(cd, ev.data);
            if (itemsToProvision.Count == 0)
            {
                _log.LogWarning("Paddle webhook: no items to provision (no custom_data and no price_id mappings).");
                return Ok(); // ack but nothing to do
            }

            // 7) Resolve user (prefer custom_data, then event emails, then API customer lookup)
            var resolvedUserId = await ResolveUserIdAsync(cd, ev.data);
            if (resolvedUserId == null)
            {
                _log.LogWarning("Paddle webhook: could not resolve user (userId={UserId}, cd.email={CdEmail}, event.email={EvEmail}, customer_id={Cust})",
                    cd?.userId, cd?.email, ev.data.customer_email ?? ev.data.customer?.email, ev.data.customer_id);
                return Ok(); // ack; nothing we can do
            }

            // 8) Persist subscriptions (idempotent) and unlock user
            foreach (var it in itemsToProvision)
            {
                var priceId  = it.PriceId;
                var careerId = it.Item.careerPathID;
                var plan     = it.Item.plan;
                var billing  = it.Item.billing;

                var exists = await _subs.UserSubscriptions.AnyAsync(s =>
                    s.UserID == resolvedUserId &&
                    s.CareerPathID == careerId &&
                    s.PaddleTransactionId == txnId);

                if (!exists)
                {
                    var sub = new UserSubscription
                    {
                        UserID              = resolvedUserId.Value,
                        CareerPathID        = careerId,
                        StartDate           = DateTime.UtcNow,
                        LastAccessedDay     = 0,
                        PaddleTransactionId = txnId,
                        PaddlePriceId       = priceId,
                        PlanName            = plan,
                        Billing             = billing
                        // EndDate computed in DB
                    };

                    _subs.UserSubscriptions.Add(sub);
                    await _subs.SaveChangesAsync();

                    _log.LogInformation("Paddle webhook: provisioned sub (user={UserId}, career={CareerId}, txn={Txn}, price={PriceId}, plan={Plan}, bill={Billing})",
                        resolvedUserId, careerId, txnId, priceId, plan, billing);
                }
                else
                {
                    _log.LogInformation("Paddle webhook: subscription already exists (user={UserId}, career={CareerId}, txn={Txn})",
                        resolvedUserId, careerId, txnId);
                }

                // Ensure the user is unlocked to this path
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

            return Ok(new { ok = true });
        }

        // ---------- Build items to provision (custom_data preferred; else map price_id from DB) ----------
        private sealed record PriceMappedItem(CustomItem Item, string? PriceId);

        private async Task<List<PriceMappedItem>> BuildProvisioningItemsAsync(CustomData? cd, Transaction data)
        {
            // 1) If custom_data.items provided, use that as source of truth
            if (cd?.items is { Count: > 0 })
            {
                var payloadPriceIds = CollectPriceIds(data);
                var mapped = new List<PriceMappedItem>();
                string? maybePriceId = payloadPriceIds.Count > 0 ? payloadPriceIds[0].priceId : null;

                foreach (var item in cd.items)
                    mapped.Add(new PriceMappedItem(item, maybePriceId));

                return mapped;
            }

            // 2) Otherwise derive from price_id → (careerPathID, plan, billing) using DB PriceMaps
            var derived  = new List<PriceMappedItem>();
            var purchased = CollectPriceIds(data);
            if (purchased.Count == 0) return derived;

            var ids = purchased.Select(p => p.priceId).ToList();

            // NOTE: Make sure your PriceMap.PaddlePriceId values are stored with the same case as Paddle (usually lower-case).
            var maps = await _subs.PriceMaps
                .Where(pm => pm.Active && ids.Contains(pm.PaddlePriceId))
                .ToListAsync();

            foreach (var (pid, qty) in purchased)
            {
                var map = maps.FirstOrDefault(m => string.Equals(m.PaddlePriceId, pid, StringComparison.OrdinalIgnoreCase));
                if (map == null)
                {
                    _log.LogWarning("Paddle webhook: no mapping configured for price_id {Pid}", pid);
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

            // Prefer details.line_items if present (includes quantity)
            if (data.details?.line_items is { Count: > 0 })
            {
                foreach (var li in data.details.line_items)
                    if (!string.IsNullOrWhiteSpace(li.price_id))
                        list.Add((li.price_id, Math.Max(1, li.quantity)));
            }

            // Fall back to items[].price.id (+ quantity if your TxnItem has it)
            else if (data.items is { Count: > 0 })
            {
                foreach (var it in data.items)
                    if (!string.IsNullOrWhiteSpace(it.price?.id))
                        list.Add((it.price.id, Math.Max(1, it.quantity)));
            }

            // Coalesce duplicates (same price multiple times)
            return list
                .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Sum(x => x.Item2)))
                .ToList();
        }

        // ---------- User resolution ----------
        private async Task<int?> ResolveUserIdAsync(CustomData? cd, Transaction data)
        {
            // 1) userId in custom_data
            if (cd?.userId is int uid && uid > 0) return uid;

            // 2) email in custom_data or event or via Paddle API
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

        // Optional: fetch email via Paddle API if we only have customer_id
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
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("Paddle API: GET customers/{Id} failed: {Code}", customerId, (int)resp.StatusCode);
                    return null;
                }

                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);

                // Expecting { "data": { "email": "..." } }
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("email", out var emailProp) &&
                    emailProp.ValueKind == JsonValueKind.String)
                {
                    var email = emailProp.GetString();
                    _log.LogInformation("Paddle API: resolved customer {Id} to email {Email}", customerId, email);
                    return email;
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
