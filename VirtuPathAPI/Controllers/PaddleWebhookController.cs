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

        // Minimal Paddle payload shapes
        private sealed record PaddleEvent(string event_id, string event_type, DateTime occurred_at, Transaction data);
        private sealed record Transaction(string id, string status, JsonElement? custom_data, List<TxnItem>? items);
        private sealed record TxnItem(TxnPrice price);
        private sealed record TxnPrice(string id);

        // Your custom_data shape from checkout
        private sealed record CustomData(int userId, List<CustomItem> items);
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
                _log.LogWarning("Invalid Paddle signature");
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
                _log.LogError(ex, "Failed to deserialize Paddle webhook");
                return BadRequest();
            }
            if (ev is null) return BadRequest();

            // 4) Only unlock on completed transactions
            if (!string.Equals(ev.event_type, "transaction.completed", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ev.data?.status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(); // ack & ignore other events
            }

            // 5) Extract our custom_data (IMPORTANT: no .Value on JsonElement)
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
                    _log.LogError(ex, "custom_data parse failed");
                }
            }

            if (cd == null || cd.userId <= 0 || cd.items == null || cd.items.Count == 0)
            {
                _log.LogWarning("Missing/invalid custom_data; nothing to provision");
                return Ok();
            }

            // Price for bookkeeping (if provided)
            var paddlePriceId = ev.data?.items?.FirstOrDefault()?.price?.id;

            // 6) Provision each purchased career (idempotent by PaddleTransactionId)
            foreach (var item in cd.items)
            {
                var careerId = item.careerPathID;

                var exists = await _subs.UserSubscriptions.AnyAsync(s =>
                    s.UserID == cd.userId &&
                    s.CareerPathID == careerId &&
                    s.PaddleTransactionId == ev.data!.id);

                if (!exists)
                {
                    var sub = new UserSubscription
                    {
                        UserID = cd.userId,
                        CareerPathID = careerId,
                        StartDate = DateTime.UtcNow,
                        LastAccessedDay = 0,
                        PaddleTransactionId = ev.data!.id,
                        PaddlePriceId = paddlePriceId,
                        PlanName = item.plan,   // <- map JSON "plan" to DB column PlanName
                        Billing  = item.billing // unchanged
                        // EndDate is computed in DB
                    };

                    _subs.UserSubscriptions.Add(sub);
                    await _subs.SaveChangesAsync();
                }

                // Always ensure the user is unlocked to this path
                var user = await _users.Users.FirstOrDefaultAsync(u => u.UserID == cd.userId);
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

        private static bool VerifyPaddleSignature(string header, string rawBody, string secret)
        {
            // Header format: ts=...;h1=...
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
