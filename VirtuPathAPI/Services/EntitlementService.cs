using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Services
{
    public class EntitlementService
    {
        private readonly UserContext _db;
        public EntitlementService(UserContext db) => _db = db;

        public async Task<bool> HasActiveAccessAsync(int userId, int careerPathId, DateTime? asOfUtc = null)
        {
            var now = asOfUtc ?? DateTime.UtcNow;

            var any = await _db.UserSubscriptions
                .Where(s => s.UserID == userId
                         && s.CareerPathID == careerPathId
                         && s.IsActive
                         && !s.IsCanceled
                         && (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= now))
                .AnyAsync();

            return any;
        }

        public async Task<UserSubscription> UpsertEntitlementAsync(
            int userId,
            int careerPathId,
            string plan,
            string billing,
            string? paddleSubId,
            string? lastTxnId,
            int termDays,
            DateTime? startAtUtc = null)
        {
            var now = DateTime.UtcNow;
            var start = startAtUtc ?? now;
            var end   = termDays > 0 ? start.AddDays(termDays) : (DateTime?)null;

            // Find existing (for this user+career+plan+billing or same Paddle sub)
            var sub = await _db.UserSubscriptions
                .Where(s => s.UserID == userId && s.CareerPathID == careerPathId)
                .Where(s => (!string.IsNullOrEmpty(paddleSubId) && s.PaddleSubscriptionId == paddleSubId)
                         || (s.Plan == plan && s.Billing == billing))
                .OrderByDescending(s => s.Id)
                .FirstOrDefaultAsync();

            if (sub == null)
            {
                sub = new UserSubscription
                {
                    UserID = userId,
                    CareerPathID = careerPathId,
                    Plan = plan,
                    Billing = billing,
                    PaddleSubscriptionId = paddleSubId,
                    LastTransactionId = lastTxnId,
                    StartAt = start,
                    CurrentPeriodEnd = end,
                    IsActive = true,
                    IsCanceled = false,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _db.UserSubscriptions.Add(sub);
            }
            else
            {
                // Extend/refresh entitlement window. If end is null (one_time lifetime), keep it null.
                sub.Plan = plan;
                sub.Billing = billing;
                sub.PaddleSubscriptionId = paddleSubId ?? sub.PaddleSubscriptionId;
                sub.LastTransactionId = lastTxnId ?? sub.LastTransactionId;
                sub.StartAt = start;
                sub.CurrentPeriodEnd = end ?? sub.CurrentPeriodEnd;
                sub.IsActive = true;
                sub.IsCanceled = false;
                sub.UpdatedAt = now;
                _db.UserSubscriptions.Update(sub);
            }

            await _db.SaveChangesAsync();
            return sub;
        }

        public async Task MarkCanceledAsync(string paddleSubId)
        {
            var now = DateTime.UtcNow;
            var subs = await _db.UserSubscriptions
                .Where(s => s.PaddleSubscriptionId == paddleSubId)
                .ToListAsync();

            foreach (var s in subs)
            {
                s.IsCanceled = true;
                s.IsActive = false;
                s.UpdatedAt = now;
            }
            await _db.SaveChangesAsync();
        }
    }
}
