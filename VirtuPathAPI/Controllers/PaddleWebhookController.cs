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
        private readonly string _frontendBase;  // e.g. https://www.virtupathai.com

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

            // Frontend base used for default redirects (when next= is missing or relative)
            _frontendBase = cfg["Frontend:BaseUrl"]?.TrimEnd('/') ?? "https://www.virtupathai.com";
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

            var (ok, why) = await ProcessTransactionAsync(ev.data);
            if (!ok) _log.LogWarning("Webhook: ProcessTransaction failed: {Why}", why ?? "(none)");
            return Ok(new { ok = ok });
        }

        // ---------------- Return endpoint ----------------
        [HttpGet("return"), AllowAnonymous]
        public async Task<IActionResult> Return(
            [FromQuery(Name = "_ptxn")] string? txnId,
            [FromQuery(Name = "transaction_id")] string? txnId2,
            [FromQuery(Name = "transactionId")] string? txnId3,
            [FromQuery] string? next)
        {
            _log.LogInformation("Paddle Return hit: {Query}", Request?.QueryString.Value);

            txnId ??= txnId2 ?? txnId3;

            if (string.IsNullOrWhiteSpace(txnId))
                return Redirect(ComposeNext(next, ok: false, msg: "no_ptxn", txnId: null));

            var tx = await FetchTransactionFromPaddleAsync(txnId);
            if (tx == null)
                return Redirect(ComposeNext(next, ok: false, msg: "fetch_failed", txnId: txnId));

            // brief retry to smooth auth lag
            if (!string.Equals(tx.status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1500);
                var tx2 = await FetchTransactionFromPaddleAsync(txnId);
                if (tx2 != null) tx = tx2;
            }

            if (!string.Equals(tx.status, "completed", StringComparison.OrdinalIgnoreCase))
                return Redirect(ComposeNext(next, ok: false, msg: "pending", txnId: txnId));

            var (ok, why) = await ProcessTransactionAsync(tx);
            if (!ok) _log.LogWarning("Return: ProcessTransaction failed: {Why}", why ?? "(none)");
            return Redirect(ComposeNext(next, ok: ok, msg: ok ? null : "provision_failed", txnId: txnId));
        }

        // Compose redirect target; defaults to frontend host
        private string ComposeNext(string? next, bool ok, string? msg, string? txnId)
        {
            string dest;
            if (string.IsNullOrWhiteSpace(next))
            {
                dest = $"{_frontendBase}/thank-you";
            }
            else if (next.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     next.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                dest = next;
            }
            else
            {
                dest = $"{_frontendBase}/{next.TrimStart('/')}";
            }

            var sep = dest.Contains('?') ? "&" : "?";
            var q = ok ? "ok=1" : "ok=0";
            if (!string.IsNullOrWhiteSpace(msg))  q += "&reason=" + Uri.EscapeDataString(msg);
            if (!string.IsNullOrWhiteSpace(txnId)) q += "&ptxn=" + Uri.EscapeDataString(txnId);
            return dest + sep + q;
        }

        // ---------------- Confirm endpoint (JSON) ----------------
        // Defensive: accepts TxnId/txnId, returns detailed error JSON on failure
        [HttpPost("confirm"), AllowAnonymous]
        [Consumes("application/json")]
        [Produces("application/json")]
        public async Task<IActionResult> Confirm([FromBody] JsonElement body)
        {
            try
            {
                string? txnId = null;
                if (body.ValueKind == JsonValueKind.Object)
                {
                    if (body.TryGetProperty("TxnId", out var a) && a.ValueKind == JsonValueKind.String)
                        txnId = a.GetString();
                    else if (body.TryGetProperty("txnId", out var b) && b.ValueKind == JsonValueKind.String)
                        txnId = b.GetString();
                }

                if (string.IsNullOrWhiteSpace(txnId))
                {
                    _log.LogWarning("Confirm: missing txnId. Raw body: {Body}", body.ToString());
                    return BadRequest(new { error = "Missing txnId" });
                }

                var tx = await FetchTransactionFromPaddleAsync(txnId);
                if (tx == null)
                    return BadRequest(new { error = "Could not fetch transaction from Paddle" });

                if (!string.Equals(tx.status, "completed", StringComparison.OrdinalIgnoreCase))
                    return Ok(new { ok = false, status = tx.status });

                var (ok, why) = await ProcessTransactionAsync(tx);
                if (!ok) return StatusCode(500, new { error = "db_update_failed", detail = why ?? "unknown" });

                return Ok(new { ok = true });
            }
            catch (DbUpdateException ex)
            {
                _log.LogError(ex, "Confirm endpoint DB update failed: {Inner}", ex.InnerException?.Message);
                return StatusCode(500, new { error = "db_update_failed", detail = ex.Message, inner = ex.InnerException?.Message });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Confirm endpoint failed");
                return StatusCode(500, new { error = "server_error", detail = ex.Message });
            }
        }

        // ---- Diagnostic probe: see raw Paddle response for a txn ----
        [HttpGet("confirm-diag"), AllowAnonymous]
        public async Task<IActionResult> ConfirmDiag([FromQuery(Name = "txnId")] string txnId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_paddleApiKey))
                    return Ok(new { ok = false, reason = "no_api_key" });

                using var http = new HttpClient { BaseAddress = new Uri("https://api.paddle.com/") };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _paddleApiKey);

                var resp = await http.GetAsync($"transactions/{txnId}");
                var text = await resp.Content.ReadAsStringAsync();

                return Ok(new { ok = resp.IsSuccessStatusCode, statusCode = (int)resp.StatusCode, body = text });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ConfirmDiag failed");
                return StatusCode(500, new { error = "server_error", detail = ex.Message });
            }
        }

        // ---------------- Core unlock logic (used by webhook, return, confirm) ----------------
        // Returns (success, reasonIfAny)
       // ---------------- Core unlock logic (used by webhook, return, confirm) ----------------
// Returns (success, reasonIfAny)
private async Task<(bool ok, string? reason)> ProcessTransactionAsync(Transaction tx)
{
    try
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
            return (false, "no_items_to_provision");
        }

        var resolvedUserId = await ResolveUserIdAsync(cd, tx);
        if (resolvedUserId == null)
        {
            _log.LogWarning("Could not resolve user for txn {Txn}", tx.id);
            return (false, "user_not_resolved");
        }

        foreach (var it in itemsToProvision)
        {
            var priceId  = it.PriceId;
            var careerId = it.Item.careerPathID;
            var plan     = it.Item.plan;
            var billing  = it.Item.billing;

            // UPSERT by (UserID, CareerPathID) to avoid unique constraint violations
            var sub = await _subs.UserSubscriptions
                .FirstOrDefaultAsync(s => s.UserID == resolvedUserId && s.CareerPathID == careerId);

            var startUtc = DateTime.UtcNow;

            if (sub == null)
            {
                sub = new UserSubscription
                {
                    UserID            = resolvedUserId.Value,
                    CareerPathID      = careerId,
                    Plan              = plan,      // "starter" | "pro" | "bonus"
                    Billing           = billing,   // "monthly" | "yearly" | "one_time"
                    StartAt           = startUtc,
                    // ⚠️ Do NOT set End/CurrentPeriodEnd here; DB computes it.
                    LastTransactionId = tx.id,
                    IsActive          = true,
                    IsCanceled        = false,
                };
                _subs.UserSubscriptions.Add(sub);

                // make sure EF doesn't think we set a computed end column
                PreventModifyingComputedEndColumns(sub);

                _log.LogInformation("Creating sub (user={UserId}, career={CareerId}, txn={Txn}, price={PriceId}, plan={Plan}, billing={Billing})",
                    resolvedUserId, careerId, tx.id, priceId, plan, billing);
            }
            else
            {
                // update/renew existing row — but do NOT touch computed end columns
                sub.Plan              = plan;
                sub.Billing           = billing;
                sub.LastTransactionId = tx.id;
                sub.IsActive          = true;
                sub.IsCanceled        = false;
                sub.StartAt           = startUtc;

                // make sure EF won’t try to update computed columns
                PreventModifyingComputedEndColumns(sub);

                _log.LogInformation("Updating sub (user={UserId}, career={CareerId}, txn={Txn}, price={PriceId}, plan={Plan}, billing={Billing})",
                    resolvedUserId, careerId, tx.id, priceId, plan, billing);
            }

            try
            {
                await _subs.SaveChangesAsync();
            }
            catch (DbUpdateException dbex)
            {
                _log.LogError(dbex, "DB error saving subscription (user={UserId}, career={CareerId}). Inner={Inner}",
                    resolvedUserId, careerId, dbex.InnerException?.Message);
                return (false, $"db_save_sub_failed: {dbex.InnerException?.Message ?? dbex.Message}");
            }

            // unlock user
            try
            {
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
            catch (DbUpdateException dbex2)
            {
                _log.LogError(dbex2, "DB error saving user unlock (user={UserId}). Inner={Inner}",
                    resolvedUserId, dbex2.InnerException?.Message);
                return (false, $"db_save_user_failed: {dbex2.InnerException?.Message ?? dbex2.Message}");
            }
        }

        return (true, null);
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "ProcessTransactionAsync failed (txn={Txn})", tx.id);
        return (false, ex.Message);
    }
}
/// <summary>
/// If the model has a computed end-date column (e.g., "EndDate" or "CurrentPeriodEnd"),
/// tell EF not to send updates for it.
/// </summary>
private void PreventModifyingComputedEndColumns(UserSubscription sub)
{
    try
    {
        var entry = _subs.Entry(sub);

        // try common names your model might use
        var prop = entry.Properties.FirstOrDefault(p =>
            string.Equals(p.Metadata.Name, "EndDate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Metadata.Name, "CurrentPeriodEnd", StringComparison.OrdinalIgnoreCase));

        if (prop != null)
        {
            prop.IsModified = false;
        }
    }
    catch
    {
        // best-effort only
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
                    var bodyText = await resp.Content.ReadAsStringAsync();
                    _log.LogWarning("Paddle API: GET transactions/{Id} failed: {Code} Body={Body}",
                        txnId, (int)resp.StatusCode, bodyText);
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
