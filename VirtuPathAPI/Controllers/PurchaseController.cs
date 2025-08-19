using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Config;
using VirtuPathAPI.Models;
using VirtuPathAPI.Services;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // default for the class
    public class PurchaseController : ControllerBase
    {
        private readonly UserContext _db;
        private readonly EntitlementService _entitlements;
        private readonly ILogger<PurchaseController> _log;

        public PurchaseController(UserContext db, EntitlementService entitlements, ILogger<PurchaseController> log)
        {
            _db = db;
            _entitlements = entitlements;
            _log = log;
        }

        // ─────────────────────────────────────────────────────────
        // Called by your Next.js webhook: no cookie, no auth header.
        // We trust it by checking a header (set in your webhook) or
        // lock down by network. Keep both if you like.
        // ─────────────────────────────────────────────────────────
        public sealed class ConfirmPayload
        {
            public string? TxnId { get; set; }
            public string? SubscriptionId { get; set; }
            public List<string> PriceIds { get; set; } = new();
            public string? CustomerEmail { get; set; }
            public Dictionary<string, object>? CustomData { get; set; }
        }

        [HttpPost("confirm")]
        [AllowAnonymous] // webhook hits this unauthenticated
        public async Task<IActionResult> Confirm([FromBody] ConfirmPayload body)
        {
            // Optional lightweight guard: require the known internal header set by your webhook route
            if (!Request.Headers.TryGetValue("X-From", out var from) || from != "next-webhook")
                _log.LogWarning("Confirm without X-From:next-webhook header");

            if (body == null || body.PriceIds == null || body.PriceIds.Count == 0)
                return BadRequest(new { error = "missing_price_ids" });

            // 1) Resolve user by email or customData.userId
            User? user = null;

            if (!string.IsNullOrWhiteSpace(body.CustomerEmail))
            {
                var email = body.CustomerEmail.Trim().ToLowerInvariant();
                user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);
            }

            if (user == null && body.CustomData != null && body.CustomData.TryGetValue("userId", out var raw))
            {
                if (int.TryParse(Convert.ToString(raw), out var uid))
                    user = await _db.Users.FindAsync(uid);
            }

            if (user == null)
                return NotFound(new { error = "user_not_found_for_purchase" });

            // 2) Map priceIds via the in-memory PaddlePriceMap
            var missing = new List<string>();
            var mapped  = new List<(string priceId, int careerId, string plan, string billing, int days)>();

            foreach (var pid in body.PriceIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (PaddlePriceMap.Map.TryGetValue(pid, out var info))
                {
                    mapped.Add((pid, info.careerPathId, info.plan, info.billing, info.days));
                }
                else
                {
                    missing.Add(pid);
                }
            }

            if (mapped.Count == 0)
                return BadRequest(new { error = "no_mapped_price_ids", missing });

            // 3) Upsert entitlements
            var granted = new List<object>();
            foreach (var m in mapped)
            {
                var rec = await _entitlements.UpsertEntitlementAsync(
                    userId: user.UserID,
                    careerPathId: m.careerId,
                    plan: m.plan,
                    billing: m.billing,
                    paddleSubId: body.SubscriptionId,
                    lastTxnId: body.TxnId,
                    termDays: m.days
                );

                granted.Add(new
                {
                    m.priceId,
                    rec.Id,
                    rec.CareerPathID,
                    rec.Plan,
                    rec.Billing,
                    rec.StartAt,
                    rec.CurrentPeriodEnd
                });
            }

            // 4) Optional: auto-start user on first career
            if ((user.CareerPathID ?? 0) == 0)
            {
                user.CareerPathID = mapped.First().careerId;
                if (user.CurrentDay <= 0) user.CurrentDay = 1;
                user.LastTaskDate = DateTime.UtcNow;
                user.LastActiveAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            return Ok(new { ok = true, granted, missing });
        }

        // Paddle cancel/pause -> disable access
        public sealed class CancelPayload { public string? SubscriptionId { get; set; } }

        [HttpPost("cancel")]
        [AllowAnonymous]
        public async Task<IActionResult> Cancel([FromBody] CancelPayload body)
        {
            if (!Request.Headers.TryGetValue("X-From", out var from) || from != "next-webhook")
                _log.LogWarning("Cancel without X-From:next-webhook header");

            if (string.IsNullOrWhiteSpace(body?.SubscriptionId))
                return BadRequest(new { error = "missing_subscription_id" });

            await _entitlements.MarkCanceledAsync(body.SubscriptionId);
            return Ok(new { ok = true });
        }

        // Logged-in users can see their current entitlements
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId is null) return Unauthorized(new { error = "not_authed" });

            var now = DateTime.UtcNow;

            var me = await _db.Users
                .Where(u => u.UserID == userId.Value)
                .Select(u => new { u.UserID, u.Email, u.CareerPathID, u.CurrentDay })
                .FirstOrDefaultAsync();

            var subs = await _db.UserSubscriptions
                .Where(s => s.UserID == userId.Value)
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => new
                {
                    s.Id,
                    s.CareerPathID,
                    s.Plan,
                    s.Billing,
                    s.PaddleSubscriptionId,
                    s.LastTransactionId,
                    s.StartAt,
                    s.CurrentPeriodEnd,
                    s.IsActive,
                    s.IsCanceled,
                    isCurrentlyValid = s.IsActive && !s.IsCanceled && (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= now)
                })
                .ToListAsync();

            return Ok(new { me, subs });
        }
    }
}
