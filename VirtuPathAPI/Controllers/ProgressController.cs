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

        public ProgressController(UserContext userContext, DailyTaskContext taskContext, TaskCompletionContext completionContext)
        {
            _userContext = userContext;
            _taskContext = taskContext;
            _completionContext = completionContext;
        }

        [HttpPost("check")]
        public async Task<IActionResult> CheckProgression([FromBody] ProgressionRequest req)
        {
            Console.WriteLine($"Received ProgressionRequest: UserID={req.UserID}, CareerPathID={req.CareerPathID}, TimeZone={req.TimeZone}");

            if (string.IsNullOrWhiteSpace(req.TimeZone))
                return BadRequest("timeZone is required.");

            TimeZoneInfo tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(req.TimeZone);
            }
            catch
            {
                return BadRequest("Invalid time zone.");
            }

            var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;

            // Load the user (this is where we track day progress)
            var user = await _userContext.Users.FirstOrDefaultAsync(u => u.UserID == req.UserID);
            if (user == null || user.CareerPathID != req.CareerPathID)
                return BadRequest("User not found or career path mismatch.");

            // Require active entitlement (subscription)
            var nowUtc = DateTime.UtcNow;
            var hasActiveEntitlement = await _completionContext.UserSubscriptions.AnyAsync(s =>
                s.UserID == req.UserID &&
                s.CareerPathID == req.CareerPathID &&
                s.IsActive &&
                !s.IsCanceled &&
                (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= nowUtc));

            if (!hasActiveEntitlement)
                return Forbid("No active subscription for this career path.");

            // Once-per-local-day gate
            if (user.LastTaskDate?.Date >= todayLocal)
                return Ok("Already progressed today.");

            // Determine the user's current day
            var day = user.CurrentDay <= 0 ? 1 : user.CurrentDay;

            // Tasks for this day
            var todaysTaskIds = await _taskContext.DailyTasks
                .Where(t => t.CareerPathID == req.CareerPathID && t.Day == day)
                .Select(t => t.TaskID)
                .ToListAsync();

            // Completed for this day (by CareerDay)
            var completedTaskIds = await _completionContext.TaskCompletions
                .Where(t => t.UserID == req.UserID && t.CareerDay == day)
                .Select(t => t.TaskID)
                .ToListAsync();

            if (todaysTaskIds.Count > 0 && todaysTaskIds.All(id => completedTaskIds.Contains(id)))
            {
                user.CurrentDay = day + 1;
                user.LastTaskDate = todayLocal;
                await _userContext.SaveChangesAsync();
                return Ok("Progressed to next day.");
            }

            return Ok("Tasks not complete. No progression.");
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
