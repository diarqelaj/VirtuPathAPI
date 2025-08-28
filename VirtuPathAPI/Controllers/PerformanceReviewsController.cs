using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PerformanceReviewsController : ControllerBase
    {
        private readonly PerformanceReviewContext _context;

        public PerformanceReviewsController(PerformanceReviewContext context)
        {
            _context = context;
        }

        // GET: api/PerformanceReviews
        [HttpGet]
        public async Task<ActionResult<IEnumerable<PerformanceReview>>> GetPerformanceReviews()
        {
            return await _context.PerformanceReviews.ToListAsync();
        }

        // GET: api/PerformanceReviews/5
        [HttpGet("{id}")]
        public async Task<ActionResult<PerformanceReview>> GetPerformanceReview(int id)
        {
            var review = await _context.PerformanceReviews.FindAsync(id);
            if (review == null)
                return NotFound();

            return review;
        }

        // POST: api/PerformanceReviews
        [HttpPost]
        public async Task<ActionResult<PerformanceReview>> CreatePerformanceReview(PerformanceReview review)
        {
            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();
            return CreatedAtAction(nameof(GetPerformanceReview), new { id = review.ReviewID }, review);
        }

        // PUT: api/PerformanceReviews/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdatePerformanceReview(int id, PerformanceReview review)
        {
            if (id != review.ReviewID)
                return BadRequest();

            _context.Entry(review).State = EntityState.Modified;
            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE: api/PerformanceReviews/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeletePerformanceReview(int id)
        {
            var review = await _context.PerformanceReviews.FindAsync(id);
            if (review == null)
                return NotFound();

            _context.PerformanceReviews.Remove(review);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpGet("progress/daily")]
        public async Task<IActionResult> GetDailyProgress(
            [FromQuery] int userId,
            [FromQuery] int day,
            [FromQuery] int? careerPathId // 👈 NEW
        )
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            // choose path: query param → fallback to user's current
            var cp = careerPathId ?? user.CareerPathID;
            if (cp == null) return BadRequest("No careerPathId provided and user has no CareerPath assigned.");
            int cpId = cp.Value;

            // tasks assigned for this path+day
            var assignedTaskIds = await _context.DailyTasks
                .Where(dt => dt.Day == day && dt.CareerPathID == cpId)
                .Select(dt => dt.TaskID)
                .ToListAsync();

            // completions for this user that match those assigned tasks
            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId && assignedTaskIds.Contains(tc.TaskID))
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            int tasksAssigned = assignedTaskIds.Count;
            int tasksCompleted = completedTaskIds.Count;
            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round(tasksCompleted * 100.0 / tasksAssigned);

            // Note: ASP.NET Core serializes to camelCase by default -> performanceScore
            return Ok(new
            {
                userId,
                careerPathId = cpId,
                day,
                tasksAssigned,
                tasksCompleted,
                performanceScore
            });
        }



       [HttpGet("progress/weekly")]
        public async Task<IActionResult> GetWeeklyProgress(
            [FromQuery] int userId,
            [FromQuery] int day,
            [FromQuery] int? careerPathId // 👈 NEW
        )
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            var cp = careerPathId ?? user.CareerPathID;
            if (cp == null) return BadRequest("No careerPathId provided and user has no CareerPath assigned.");
            int cpId = cp.Value;

            int weekStart = ((day - 1) / 7) * 7 + 1;
            int weekEnd = weekStart + 6;

            var assigned = await _context.DailyTasks
                .Where(dt => dt.CareerPathID == cpId && dt.Day >= weekStart && dt.Day <= weekEnd)
                .Select(dt => dt.TaskID)
                .ToListAsync();

            var completed = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId && assigned.Contains(tc.TaskID))
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            int tasksAssigned = assigned.Count;
            int tasksCompleted = completed.Count;
            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round(tasksCompleted * 100.0 / tasksAssigned);

            return Ok(new
            {
                userId,
                careerPathId = cpId,
                weekRange = $"Days {weekStart}-{weekEnd}",
                tasksAssigned,
                tasksCompleted,
                performanceScore
            });
        }



       // GET: api/PerformanceReviews/progress/monthly?userId=1&careerPathId=123
[HttpGet("progress/monthly")]
public async Task<IActionResult> GetMonthlyProgress(
    [FromQuery] int userId,
    [FromQuery] int? careerPathId // 👈 optional; falls back to user's current
)
{
    // 1) Resolve user and career path
    var user = await _context.Users.FindAsync(userId);
    if (user == null)
        return NotFound("User not found.");

    var cp = careerPathId ?? user.CareerPathID;
    if (cp == null)
        return BadRequest("No careerPathId provided and user has no CareerPath assigned.");
    int cpId = cp.Value;

    // 2) Load all tasks for this path within 1–30 (fixed month window)
    //    Keeping full objects so we can reuse Day for weekly breakdown.
    var allTasksInMonth = await _context.DailyTasks
        .Where(dt => dt.CareerPathID == cpId && dt.Day >= 1 && dt.Day <= 30)
        .ToListAsync();

    // 3) Load user's completed TaskIDs that intersect those month tasks (same user, this path)
    var monthTaskIds = allTasksInMonth.Select(t => t.TaskID).ToList();
    var completedTaskIds = await _context.TaskCompletions
        .Where(tc => tc.UserID == userId && monthTaskIds.Contains(tc.TaskID))
        .Select(tc => tc.TaskID)
        .Distinct()
        .ToListAsync();

    // 4) Totals for the month
    int totalAssigned = allTasksInMonth.Count;
    int totalCompleted = completedTaskIds.Count;
    int performanceScore = totalAssigned == 0
        ? 0
        : (int)Math.Round(totalCompleted * 100.0 / totalAssigned);

    // 5) Weekly breakdown (weeks 1..5, days 1..30)
    var weeklyProgress = new List<object>(capacity: 5);
    for (int i = 0; i < 5; i++)
    {
        int startDay = i * 7 + 1;
        int endDay = Math.Min(startDay + 6, 30);

        var weekTasks = allTasksInMonth.Where(t => t.Day >= startDay && t.Day <= endDay).ToList();
        var weekTaskIds = weekTasks.Select(t => t.TaskID).ToList();
        int weekTotal = weekTasks.Count;
        int weekCompleted = weekTaskIds.Count(id => completedTaskIds.Contains(id));

        weeklyProgress.Add(new
        {
            Week = $"Week {i + 1}",
            Completed = weekCompleted,
            Total = weekTotal
        });
    }

    // 6) Response (ASP.NET Core will camelCase these by default)
    return Ok(new
    {
        UserID = userId,
        CareerPathID = cpId,
        Month = DateTime.UtcNow.Month,
        Year = DateTime.UtcNow.Year,
        TasksAssigned = totalAssigned,
        TasksCompleted = totalCompleted,
        PerformanceScore = performanceScore,
        WeeklyProgress = weeklyProgress
    });
}
// GET: api/PerformanceReviews/progress/alltime?userId=1&careerPathId=123
[HttpGet("progress/alltime")]
public async Task<IActionResult> GetAllTimeProgress(
    [FromQuery] int userId,
    [FromQuery] int? careerPathId // 👈 optional; falls back to user's current
)
{
    var user = await _context.Users.FindAsync(userId);
    if (user == null)
        return NotFound("User not found.");

    var cp = careerPathId ?? user.CareerPathID;
    if (cp == null)
        return BadRequest("No careerPathId provided and user has no CareerPath assigned.");
    int cpId = cp.Value;

    // Load ALL tasks for this path across days 1..360 (12 "months" of 30 days)
    // Keep full objects so we can slice by day ranges in-memory efficiently.
    var allTasks = await _context.DailyTasks
        .Where(dt => dt.CareerPathID == cpId && dt.Day >= 1 && dt.Day <= 360)
        .ToListAsync();

    var allTaskIds = allTasks.Select(t => t.TaskID).ToList();

    // Only completions that correspond to this path's tasks
    var completedTaskIds = await _context.TaskCompletions
        .Where(tc => tc.UserID == userId && allTaskIds.Contains(tc.TaskID))
        .Select(tc => tc.TaskID)
        .Distinct()
        .ToListAsync();

    var monthlyProgress = new List<object>(capacity: 12);

    for (int month = 0; month < 12; month++)
    {
        int startDay = month * 30 + 1;
        int endDay = startDay + 29;

        var monthTasks = allTasks.Where(dt => dt.Day >= startDay && dt.Day <= endDay).ToList();
        var monthTaskIds = monthTasks.Select(t => t.TaskID).ToList();

        int tasksAssigned = monthTasks.Count;
        int tasksCompleted = monthTaskIds.Count(id => completedTaskIds.Contains(id));

        monthlyProgress.Add(new
        {
            Month = $"Month {month + 1}",
            Days = $"{startDay}-{endDay}",
            Completed = tasksCompleted,
            Total = tasksAssigned
        });
    }

    return Ok(new
    {
        UserID = userId,
        CareerPathID = cpId,
        MonthlyProgress = monthlyProgress
    });
}


        // POST: api/PerformanceReviews/generate-by-day?userId=1&day=3
        [HttpPost("generate-by-day")]
        public async Task<IActionResult> GeneratePerformanceReviewByDay([FromQuery] int userId, [FromQuery] int day)
        {
            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            var completedOnDay = await _context.DailyTasks
                .Where(dt => dt.Day == day && completedTaskIds.Contains(dt.TaskID))
                .ToListAsync();

            var careerPathId = completedOnDay.FirstOrDefault()?.CareerPathID ?? 0;

            var assignedOnDay = await _context.DailyTasks
                .Where(dt => dt.Day == day && dt.CareerPathID == careerPathId)
                .ToListAsync();

            int tasksCompleted = completedOnDay.Count;
            int tasksAssigned = assignedOnDay.Count;

            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            var review = new PerformanceReview
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = DateTime.UtcNow.Month,
                Year = DateTime.UtcNow.Year,
                TasksCompleted = tasksCompleted,
                TasksAssigned = tasksAssigned,
                PerformanceScore = performanceScore
            };

            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();

            return Ok(review);
        }

        // POST: api/PerformanceReviews/generate-weekly?userId=1
        [HttpPost("generate-weekly")]
        public async Task<IActionResult> GenerateWeeklyPerformance([FromQuery] int userId)
        {
            var currentMonth = DateTime.UtcNow.Month;
            var currentYear = DateTime.UtcNow.Year;

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            var weeklyTasks = await _context.DailyTasks
                .Where(dt => dt.Day >= 1 && dt.Day <= 7)  // First week (Days 1-7)
                .ToListAsync();

            var careerPathId = weeklyTasks.FirstOrDefault()?.CareerPathID ?? 0;

            var assignedTasks = weeklyTasks.Where(dt => dt.CareerPathID == careerPathId).ToList();
            var completedTasks = assignedTasks.Where(t => completedTaskIds.Contains(t.TaskID)).ToList();

            int tasksAssigned = assignedTasks.Count;
            int tasksCompleted = completedTasks.Count;

            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            var review = new PerformanceReview
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = currentMonth,
                Year = currentYear,
                TasksCompleted = tasksCompleted,
                TasksAssigned = tasksAssigned,
                PerformanceScore = performanceScore
            };

            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();

            return Ok(review);
        }

        [HttpGet("user/{userId}")]
        public async Task<ActionResult<List<PerformanceReview>>> GetReviewsForUser(int userId)
        {
            var reviews = await _context.PerformanceReviews
                .Where(r => r.UserID == userId)
                .ToListAsync();

            if (reviews == null || reviews.Count == 0)
                return NotFound($"No performance reviews found for user {userId}.");

            return Ok(reviews);
        }
        // POST: api/PerformanceReviews/generate-monthly?userId=1
        [HttpPost("generate-monthly")]
        public async Task<IActionResult> GenerateMonthlyPerformance([FromQuery] int userId)
        {
            var currentMonth = DateTime.UtcNow.Month;
            var currentYear = DateTime.UtcNow.Year;

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            var monthlyTasks = await _context.DailyTasks
                .Where(dt => dt.Day >= 1 && dt.Day <= 30)  // For simplicity, assuming 30 days in month
                .ToListAsync();

            var careerPathId = monthlyTasks.FirstOrDefault()?.CareerPathID ?? 0;

            var assignedTasks = monthlyTasks.Where(dt => dt.CareerPathID == careerPathId).ToList();
            var completedTasks = assignedTasks.Where(t => completedTaskIds.Contains(t.TaskID)).ToList();

            int tasksAssigned = assignedTasks.Count;
            int tasksCompleted = completedTasks.Count;

            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            var review = new PerformanceReview
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = currentMonth,
                Year = currentYear,
                TasksCompleted = tasksCompleted,
                TasksAssigned = tasksAssigned,
                PerformanceScore = performanceScore
            };

            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();

            return Ok(review);
        }
    }
}
