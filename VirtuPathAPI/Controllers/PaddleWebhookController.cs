using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        public PaddleWebhookController(
            UserContext users,
            UserSubscriptionContext subs,
            ILogger<PaddleWebhookController> log,
            IConfiguration cfg)
        {
            _users = users;
            _subs = subs;
            _log = log;
            _secret = cfg["Paddle:WebhookSecret"] ?? throw new InvalidOperationException("Missing Paddle:WebhookSecret");
        }

        // Minimal Paddle payload shapes (add customer_email & customer.email as fallbacks)
        private sealed record PaddleEvent(string event_id, string event_type, DateTime occurred_at, Transaction data);
        private sealed record Transaction(
            string id,
            string status,
            JsonElement? custom_data,
            List<TxnItem>? items,
            string? customer_email,   // <-- fallback A
            Customer? customer        // <-- fallback B
        );
        private sealed record Customer(string? email);
        private sealed record TxnItem(TxnPrice price);
        private sealed record TxnPrice(string id);

        // Your custom_data shape from checkout (add email)
        private sealed record CustomData(int? userId, string? email, List<CustomItem> items);
        private sealed record CustomItem(int careerPathID, string plan, string billing, int quantity);

        [HttpPost]
        public async Task<IActionResult> Receive()
        {
            // 1) Read raw body (for signature verification)
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            var raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            // 2) Verify HMAC signature
            var sigHeader = Request.Headers["Paddle-Signature"].ToString();
            if (!VerifyPaddleSignature(sigHeader, raw, _secret))
            {
                _log.LogWarning("Paddle webhook: invalid signature");
                return Unauthorized();
            }

            // 3) Deserialize
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
            if (ev is null) return BadRequest();

            // 4) Only unlock on completed transactions
            if (!string.Equals(ev.event_type, "transaction.completed", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ev.data?.status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Paddle webhook: ignoring event {Type} with status {Status}", ev.event_type, ev.data?.status);
                return Ok();
            }

            // 5) Extract our custom_data
            CustomData? cd = null;
            if (ev.data?.custom_data is JsonElement ce && ce.ValueKind != JsonValueKind.Null)
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

            // Resolve user by userId OR email
            var resolvedUserId = await ResolveUserIdAsync(cd, ev.data);
            if (resolvedUserId == null)
            {
                _log.LogWarning("Paddle webhook: could not resolve user (userId={UserId}, cd.email={CdEmail}, event.email={EvEmail})",
                    cd?.userId, cd?.email, ev.data?.customer_email ?? ev.data?.customer?.email);
                return Ok(); // ack but nothing to provision
            }

            if (cd?.items == null || cd.items.Count == 0)
            {
                _log.LogWarning("Paddle webhook: no items in custom_data for user {UserId}", resolvedUserId);
                return Ok();
            }

            // Price for bookkeeping (if provided)
            var paddlePriceId = ev.data?.items?.FirstOrDefault()?.price?.id;

            // 6) Provision each purchased career (idempotent on transaction id)
            foreach (var item in cd.items)
            {
                var careerId = item.careerPathID;

                var exists = await _subs.UserSubscriptions.AnyAsync(s =>
                    s.UserID == resolvedUserId &&
                    s.CareerPathID == careerId &&
                    s.PaddleTransactionId == ev.data!.id);

                if (!exists)
                {
                    var sub = new UserSubscription
                    {
                        UserID = resolvedUserId.Value,
                        CareerPathID = careerId,
                        StartDate = DateTime.UtcNow,
                        LastAccessedDay = 0,
                        PaddleTransactionId = ev.data!.id,
                        PaddlePriceId = paddlePriceId,
                        PlanName = item.plan,   // DB column is PlanName
                        Billing  = item.billing
                        // EndDate is computed in DB
                    };

                    _subs.UserSubscriptions.Add(sub);
                    await _subs.SaveChangesAsync();

                    _log.LogInformation("Paddle webhook: provisioned sub (user={UserId}, career={CareerId}, txn={Txn})",
                        resolvedUserId, careerId, ev.data!.id);
                }
                else
                {
                    _log.LogInformation("Paddle webhook: subscription already exists (user={UserId}, career={CareerId}, txn={Txn})",
                        resolvedUserId, careerId, ev.data!.id);
                }

                // Ensure user is unlocked to this path
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

        private async Task<int?> ResolveUserIdAsync(CustomData? cd, Transaction? data)
        {
            if (cd?.userId is int uid && uid > 0) return uid;

            string? email =
                cd?.email?.Trim() ??
                data?.customer_email?.Trim() ??
                data?.customer?.email?.Trim();

            if (string.IsNullOrEmpty(email)) return null;

            email = email.ToLowerInvariant();
            var user = await _users.Users
                .Where(u => u.Email.ToLower() == email)
                .Select(u => new { u.UserID })
                .FirstOrDefaultAsync();

            return user?.UserID;
        }

        private static bool VerifyPaddleSignature(string header, string rawBody, string secret)
        {
            if (string.IsNullOrWhiteSpace(header)) return false;

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
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hex),
                Encoding.ASCII.GetBytes(h1));
        }
    }
}
