using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProgressController : ControllerBase
    {
        private readonly UserContext _userContext;
        private readonly DailyTaskContext _taskContext;
        private readonly TaskCompletionContext _completionContext;

        public ProgressController(
            UserContext userContext,
            DailyTaskContext taskContext,
            TaskCompletionContext completionContext)
        {
            _userContext = userContext;
            _taskContext = taskContext;
            _completionContext = completionContext;
        }

        // ---- Config ----
        private const int GRACE_HOURS_DEFAULT = 2; // 2am cutoff

        private static TimeZoneInfo ResolveTz(string? tz)
        {
            if (string.IsNullOrWhiteSpace(tz)) return TimeZoneInfo.Utc;
            try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
            catch { return TimeZoneInfo.Utc; }
        }

        // Local learning "date" = (local now - graceHours).Date
        private static DateTime ComputeLearningDate(TimeZoneInfo tz, int graceHours)
        {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            return localNow.AddHours(-graceHours).Date;
        }

        private async Task<int> MaxDayForPathAsync(int careerPathId)
        {
            return await _taskContext.DailyTasks.AsNoTracking()
                   .Where(t => t.CareerPathID == careerPathId)
                   .Select(t => (int?)t.Day)
                   .MaxAsync() ?? 1;
        }

        private async Task<(int assigned, int completed)> GetCountsAsync(int userId, int careerPathId, int day)
        {
            var assigned = await _taskContext.DailyTasks.AsNoTracking()
                              .CountAsync(t => t.CareerPathID == careerPathId && t.Day == day);

            // Fast if TaskCompletion has CareerPathID
            var completed = await _completionContext.TaskCompletions.AsNoTracking()
                              .CountAsync(tc => tc.UserID == userId
                                             && tc.CareerPathID == careerPathId
                                             && tc.CareerDay == day);
            return (assigned, completed);
        }

        private static int NeededFor50(int assigned)
            => (int)Math.Ceiling(assigned * 0.5);

        // READ-ONLY: returns display day (respects midnight+grace and 50% rule, but does not mutate DB)
        private async Task<int> ResolveCurrentDayAsync(int userId, int careerPathId, TimeZoneInfo tz, int graceHours)
        {
            var user = await _userContext.Users.FindAsync(userId);
            if (user == null) return 1;

            var nowUtc = DateTime.UtcNow;
            var entitled = await _completionContext.UserSubscriptions.AnyAsync(s =>
                s.UserID == userId && s.CareerPathID == careerPathId &&
                (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= nowUtc));
            if (!entitled) return 1;

            var learningDate = ComputeLearningDate(tz, graceHours);

            int day = user.CurrentDay <= 0 ? 1 : user.CurrentDay;
            bool crossedBoundary = user.LastTaskDate == null || user.LastTaskDate.Value.Date < learningDate;

            if (crossedBoundary)
            {
                var (assigned, completed) = await GetCountsAsync(userId, careerPathId, day);
                if (assigned > 0 && completed >= NeededFor50(assigned)) day += 1;
            }

            int maxDay = await MaxDayForPathAsync(careerPathId);
            return Math.Clamp(day, 1, maxDay);
        }
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary(
            [FromQuery] int userId,
            [FromQuery] int careerPathId)
        {
            var user = await _userContext.Users.AsNoTracking()
                        .FirstOrDefaultAsync(u => u.UserID == userId);
            if (user == null) return NotFound("User not found.");

            // how many days exist in this path?
            int totalDays = await MaxDayForPathAsync(careerPathId);

            // clamp current day to a valid range
            int currentDay = Math.Clamp(user.CurrentDay <= 0 ? 1 : user.CurrentDay, 1, totalDays);

            // days view: “how many full days are completed?”
            int daysCompleted = Math.Clamp(currentDay - 1, 0, totalDays);

            // tasks view (optional; keep for other widgets)
            int tasksTotal = await _taskContext.DailyTasks.AsNoTracking()
                            .CountAsync(t => t.CareerPathID == careerPathId);

            int tasksCompleted = await _completionContext.TaskCompletions.AsNoTracking()
                                .CountAsync(tc => tc.UserID == userId
                                                && tc.CareerPathID == careerPathId);

            return Ok(new
            {
                currentDay,          // e.g., 1
                totalDays,           // e.g., 365
                daysCompleted,       // e.g., 0 (means 0/365 days done)
                tasksCompleted,      // e.g., 1
                tasksTotal           // e.g., 1093
            });
        }


        [HttpGet("current")]
        public async Task<IActionResult> GetCurrent(
            [FromQuery] int userId,
            [FromQuery] int careerPathId,
            [FromQuery] string? tz = null,
            [FromQuery] int graceHours = GRACE_HOURS_DEFAULT)
        {
            var user = await _userContext.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            // optional entitlement gate
            var nowUtc = DateTime.UtcNow;
            var entitled = await _completionContext.UserSubscriptions.AnyAsync(s =>
                s.UserID == userId &&
                s.CareerPathID == careerPathId &&
                (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= nowUtc));
            if (!entitled) return Forbid("No active subscription for this career path.");

            var tzinfo = ResolveTz(tz);
            var learningDate = ComputeLearningDate(tzinfo, graceHours);

            int day = user.CurrentDay <= 0 ? 1 : user.CurrentDay;
            bool crossedBoundary = user.LastTaskDate == null || user.LastTaskDate.Value.Date < learningDate;

            if (crossedBoundary)
            {
                var (assigned, completed) = await GetCountsAsync(userId, careerPathId, day);
                if (assigned > 0 && completed >= NeededFor50(assigned))
                    day += 1; // eligible to *see* next day after boundary
            }

            int maxDay = await MaxDayForPathAsync(careerPathId);
            day = Math.Clamp(day, 1, maxDay);

            return Ok(new { currentDay = day });
        }

        [HttpPost("check")]
        public async Task<IActionResult> CheckProgression(
            [FromBody] ProgressionRequest req,
            [FromQuery] int graceHours = 2)
        {
            if (string.IsNullOrWhiteSpace(req.TimeZone))
                return BadRequest("timeZone is required.");

            var tzinfo = ResolveTz(req.TimeZone);
            var learningDate = ComputeLearningDate(tzinfo, graceHours);

            var user = await _userContext.Users.FirstOrDefaultAsync(u => u.UserID == req.UserID);
            if (user == null || user.CareerPathID != req.CareerPathID)
                return BadRequest("User not found or career path mismatch.");

            var nowUtc = DateTime.UtcNow;
            var entitled = await _completionContext.UserSubscriptions.AnyAsync(s =>
                s.UserID == req.UserID &&
                s.CareerPathID == req.CareerPathID &&
                (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= nowUtc));
            if (!entitled) return Forbid("No active subscription for this career path.");

            int maxDay = await MaxDayForPathAsync(req.CareerPathID);
            int day = Math.Clamp(user.CurrentDay <= 0 ? 1 : user.CurrentDay, 1, maxDay);

            var (assigned, completed) = await GetCountsAsync(req.UserID, req.CareerPathID, day);
            int needed = NeededFor50(assigned);

            bool alreadyStamped = user.LastTaskDate?.Date >= learningDate;

            // Only progress once per learning date, and only if >=50%
            if (!alreadyStamped && assigned > 0 && completed >= needed)
            {
                user.CurrentDay = Math.Min(day + 1, maxDay);
                user.LastTaskDate = learningDate;
                await _userContext.SaveChangesAsync();

                return Ok(new
                {
                    progressed = true,
                    newDay = user.CurrentDay,
                    assigned,
                    completed,
                    needed,
                    learningDate
                });
            }

            // Not enough yet (or already progressed): do NOT stamp LastTaskDate.
            // Allows user to finish more tasks and call /check again this same learning date.
            return Ok(new
            {
                progressed = false,
                reason = alreadyStamped
                    ? "Already progressed for this learning day."
                    : "Below 50% threshold.",
                assigned,
                completed,
                needed,
                learningDate
            });
        }

    }

    public class ProgressionRequest
    {
        [JsonPropertyName("userID")]
        public int UserID { get; set; }

        [JsonPropertyName("careerPathID")]
        public int CareerPathID { get; set; }

        [JsonPropertyName("timeZone")]
        public string TimeZone { get; set; } = string.Empty;
    }
}
