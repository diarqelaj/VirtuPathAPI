using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Claims;

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
        // GET: api/PerformanceReviews/progress/weekly-breakdown?userId=1&day=10&careerPathId=123
[HttpGet("progress/weekly-breakdown")]
public async Task<IActionResult> GetWeeklyBreakdown(
    [FromQuery] int userId,
    [FromQuery] int day,
    [FromQuery] int? careerPathId)
{
    // 0) Resolve user & career path (consistent with your other methods)
    var user = await _context.Users.FindAsync(userId);
    if (user == null) return NotFound("User not found.");

    var cp = careerPathId ?? user.CareerPathID;
    if (cp == null) return BadRequest("No careerPathId provided and user has no CareerPath assigned.");
    int cpId = cp.Value;

    // 1) Compute week window by career day (e.g., days 8..14 if day=10)
    int weekStart = ((day - 1) / 7) * 7 + 1;
    int weekEnd   = weekStart + 6;

    // 2) Pull all tasks for this path in the week
    var weekTasks = await _context.DailyTasks
        .Where(dt => dt.CareerPathID == cpId && dt.Day >= weekStart && dt.Day <= weekEnd)
        .Select(dt => new { dt.TaskID, dt.Day })
        .ToListAsync();

    var weekTaskIds = weekTasks.Select(t => t.TaskID).ToList();

    // 3) Pull user completions for ONLY those tasks (any time in the past)
    var completedIds = await _context.TaskCompletions
        .Where(tc => tc.UserID == userId && weekTaskIds.Contains(tc.TaskID))
        .Select(tc => tc.TaskID)
        .Distinct()
        .ToListAsync();

    // 4) Aggregate per day (assigned count; completed count)
    var byDay = weekTasks
        .GroupBy(t => t.Day)
        .Select(g => new
        {
            Day = g.Key,
            Assigned = g.Count(),
            Completed = g.Count(t => completedIds.Contains(t.TaskID))
        })
        .OrderBy(x => x.Day)
        .ToList();

    // Ensure 7 rows even if no tasks exist some days
    var result = Enumerable.Range(0, 7)
        .Select(i => {
            int d = weekStart + i;
            var row = byDay.FirstOrDefault(x => x.Day == d);
            return new
            {
                careerDay = d,
                assigned  = row?.Assigned  ?? 0,
                completed = row?.Completed ?? 0
            };
        })
        .ToList();

    return Ok(new {
        userId,
        careerPathId = cpId,
        weekStart,
        weekEnd,
        days = result   // length 7, ordered by careerDay
    });
}
    [HttpGet("progress/weekly-by-calendar")]
public async Task<IActionResult> GetWeeklyByCalendar(
    [FromQuery] int? userId,
    [FromQuery] int? careerPathId,
    [FromQuery] string? timeZone = "Europe/Belgrade",
    [FromQuery] bool mondayStart = true)
{
    // 1) Try resolve userId from session/claims if not provided
    int? resolvedUserId = userId;
    if (resolvedUserId is null or <= 0)
    {
        var sessionUid = HttpContext.Session.GetInt32("UserID");
        if (sessionUid.HasValue) resolvedUserId = sessionUid.Value;
        else if (User?.Identity?.IsAuthenticated == true)
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("sub")?.Value;
            if (int.TryParse(claim, out var fromClaim)) resolvedUserId = fromClaim;
        }
    }

    // 2) Try load user if we have an id
    User? user = null;
    if (resolvedUserId is > 0)
    {
        user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserID == resolvedUserId.Value);
    }

    // 3) Resolve career path:
    //    - prefer explicit query
    //    - otherwise user’s current career path
    //    - if neither exist -> 400
    int? cp = careerPathId ?? user?.CareerPathID;
    if (cp == null)
        return BadRequest("No careerPathId provided and user has no CareerPath assigned.");
    int cpId = cp.Value;

    // 4) If we still have no valid user, don't hard-fail: continue with 0 completions
    var uid = resolvedUserId ?? 0;

    // 5) Timezone resolution (safe)
    TimeZoneInfo tz;
    try
    {
        tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "Europe/Belgrade");
    }
    catch
    {
        tz = TimeZoneInfo.Utc;
    }
    var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);

    // 6) Start of week (local)
    int dow = (int)nowLocal.DayOfWeek; // 0=Sun..6=Sat
    int offsetToMon = mondayStart ? (dow == 0 ? -6 : 1 - dow) : -dow;
    var weekStartLocal = nowLocal.Date.AddDays(offsetToMon);
    var daysLocal = Enumerable.Range(0, 7)
        .Select(i => weekStartLocal.AddDays(i).Date)
        .ToList();

    // 7) Preload assigned counts per careerDay
    var tasksByDay = await _context.DailyTasks
    .AsNoTracking()
    .Where(dt => dt.CareerPathID == cpId)
    .GroupBy(dt => dt.Day)
    .Select(g => new { Day = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.Day, x => x.Count);


    // 8) Pull completions for the local week window (only if we have a user)
    var weekStartUtc = TimeZoneInfo.ConvertTimeToUtc(weekStartLocal, tz);
    var weekEndUtc   = TimeZoneInfo.ConvertTimeToUtc(weekStartLocal.AddDays(7), tz);

    List<CompletionLite> completions = (uid > 0)
    ? await _context.TaskCompletions
        .Where(tc => tc.UserID == uid && tc.CareerPathID == cpId
                     && tc.CompletionDate >= weekStartUtc
                     && tc.CompletionDate <  weekEndUtc)
        .Select(tc => new CompletionLite
        {
            TaskID = tc.TaskID,
            CareerDay = tc.CareerDay,
            CompletionDate = tc.CompletionDate
        })
        .AsNoTracking()
        .ToListAsync()
    : new List<CompletionLite>();


    var completionsByLocalDate = completions
        .GroupBy(c => TimeZoneInfo.ConvertTime(c.CompletionDate, tz).Date)
        .ToDictionary(g => g.Key, g => g.ToList());

    // 9) Baseline career day at week start:
    //    - if we saw any completions in this week -> min CareerDay
    //    - else, approximate by the *latest* completion in the past (if any)
    //      so we don’t default to Day 1 forever.
   int baselineDay = completions.Any() ? completions.Min(c => c.CareerDay) : 1;

    if (completions.Any())
    {
        baselineDay = completions.Min(c => (int)c.CareerDay);
    }
    else if (uid > 0)
    {
        var last = await _context.TaskCompletions
            .Where(tc => tc.UserID == uid && tc.CareerPathID == cpId)
            .OrderByDescending(tc => tc.CompletionDate)
            .Select(tc => tc.CareerDay)
            .FirstOrDefaultAsync();

        if (last > 0) baselineDay = last; // stay on last known career day
    }

    // 10) Build rows with 50% advance rule
    var result = new List<object>(7);
    int currentCareerDay = baselineDay;

    foreach (var localDate in daysLocal)
    {
        int assigned = tasksByDay.TryGetValue(currentCareerDay, out var cnt) ? cnt : 0;

        int completed = 0;
        if (completionsByLocalDate.TryGetValue(localDate, out var list))
            completed = list.Count(c => c.CareerDay == currentCareerDay);

        result.Add(new
        {
            localDate = localDate.ToString("yyyy-MM-dd"),
            careerDay = currentCareerDay,
            assigned,
            completed
        });

        // advance only if ≥50% complete
        if (assigned > 0 && completed * 2 >= assigned)
            currentCareerDay += 1;
    }

    return Ok(result);
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
        internal sealed class CompletionLite
    {
        public int TaskID { get; set; }
        public int CareerDay { get; set; }
        public DateTime CompletionDate { get; set; }
    }
    }
}
