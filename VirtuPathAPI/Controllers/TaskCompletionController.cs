using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using System.Globalization;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TaskCompletionController : ControllerBase
    {
        private readonly TaskCompletionContext _context;

        public TaskCompletionController(TaskCompletionContext context)
        {
            _context = context;
        }

        /// <summary>
        /// GET /api/TaskCompletion?userID=..&careerPathId=.. (both optional filters)
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<TaskCompletion>>> GetTaskCompletions(
            [FromQuery] int? userID,
            [FromQuery] int? careerPathId)
        {
            var q = _context.TaskCompletions.AsQueryable();

            if (userID.HasValue) q = q.Where(tc => tc.UserID == userID.Value);
            if (careerPathId.HasValue) q = q.Where(tc => tc.CareerPathID == careerPathId.Value);

            return Ok(await q.ToListAsync());
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<TaskCompletion>> GetTaskCompletion(int id)
        {
            var completion = await _context.TaskCompletions.FindAsync(id);
            if (completion == null)
                return NotFound();

            return completion;
        }

        /// <summary>
        /// Create a completion. 
        /// Body must include: UserID, TaskID, CareerPathID, CareerDay. 
        /// We IGNORE client-provided CompletionDate and compute server-side.
        /// Optional: ?timeZone=Europe/Belgrade (defaults to Europe/Belgrade)
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<TaskCompletion>> CreateTaskCompletion(
            [FromBody] TaskCompletion completion,
            [FromQuery] string? timeZone = "Europe/Belgrade")
        {
            if (completion == null) return BadRequest("Missing payload.");
            if (completion.UserID <= 0 || completion.TaskID <= 0 || completion.CareerPathID <= 0)
                return BadRequest("UserID, TaskID and CareerPathID are required.");
            if (completion.CareerDay < 0) return BadRequest("CareerDay must be >= 0.");

            // Optional: validate the task belongs to the given careerPathId
            var task = await _context.DailyTasks
                .Where(t => t.TaskID == completion.TaskID)
                .Select(t => new { t.TaskID, t.CareerPathID, t.Day })
                .FirstOrDefaultAsync();

            if (task == null) return BadRequest("Task does not exist.");
            if (task.CareerPathID != completion.CareerPathID)
                return BadRequest("Task does not belong to the specified CareerPathID.");

            // Optional: prevent duplicates (same user+task)
            var already = await _context.TaskCompletions
                .AnyAsync(tc => tc.UserID == completion.UserID && tc.TaskID == completion.TaskID);
            if (already)
                return Conflict("Task already marked as completed.");

            // ---- NEW: set completion timestamps server-side (UTC + LocalDate) ----
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "Europe/Belgrade"); }
            catch { return BadRequest("Invalid timeZone id."); }

            var utcNow   = DateTime.UtcNow;
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);

            completion.CompletedAtUtc     = utcNow;
            completion.CompletedLocalDate = localNow.Date;

            // Keep existing field populated for backward compatibility
            // If your model's CompletionDate was intended as "when completed", populate it here:
            completion.CompletionDate = localNow;

            _context.TaskCompletions.Add(completion);
            await _context.SaveChangesAsync();

            // Non-blocking: Update / upsert monthly performance review for this *careerPath*
            try
            {
                // Use the computed local date for month/year
                int month = localNow.Month;
                int year  = localNow.Year;

                // NOTE: This "assigned" logic is naive (total tasks of the path).
                // If you want "assigned this month", adjust accordingly.
                int totalAssignedTasks = await _context.DailyTasks
                    .Where(t => t.CareerPathID == completion.CareerPathID)
                    .CountAsync();

                var existingReview = await _context.PerformanceReviews.FirstOrDefaultAsync(r =>
                    r.UserID == completion.UserID &&
                    r.CareerPathID == completion.CareerPathID &&
                    r.Month == month &&
                    r.Year == year
                );

                if (existingReview != null)
                {
                    existingReview.TasksCompleted += 1;
                    existingReview.PerformanceScore = existingReview.TasksAssigned > 0
                        ? (int)Math.Round((double)existingReview.TasksCompleted / existingReview.TasksAssigned * 100)
                        : 0;

                    _context.PerformanceReviews.Update(existingReview);
                }
                else
                {
                    var review = new PerformanceReview
                    {
                        UserID = completion.UserID,
                        CareerPathID = completion.CareerPathID,
                        Month = month,
                        Year = year,
                        TasksCompleted = 1,
                        TasksAssigned = totalAssignedTasks,
                        PerformanceScore = totalAssignedTasks > 0
                            ? (int)Math.Round(100.0 / totalAssignedTasks)
                            : 0
                    };

                    await _context.PerformanceReviews.AddAsync(review);
                }

                await _context.SaveChangesAsync();
            }
            catch
            {
                // swallow non-critical analytics errors
            }

            return CreatedAtAction(nameof(GetTaskCompletion), new { id = completion.CompletionID }, completion);
        }

        [HttpGet("byuser/{userId}")]
        public async Task<ActionResult<IEnumerable<TaskCompletion>>> GetTaskCompletionsByUser(int userId)
        {
            return await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .ToListAsync();
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateTaskCompletion(int id, TaskCompletion completion)
        {
            if (id != completion.CompletionID)
                return BadRequest();

            _context.Entry(completion).State = EntityState.Modified;
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteTaskCompletion(int id)
        {
            var completion = await _context.TaskCompletions.FindAsync(id);
            if (completion == null)
                return NotFound();

            _context.TaskCompletions.Remove(completion);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        // ---------------------------
        // NEW: weekly progress by LOCAL calendar date (Mon..Sun)
        // ---------------------------
        [HttpGet("weekly-by-date")]
        public async Task<IActionResult> WeeklyByDate(
            [FromQuery] int userId,
            [FromQuery] int careerPathId,
            [FromQuery] string? timeZone = "Europe/Belgrade",
            [FromQuery] DateTime? anchorLocalDate = null,
            [FromQuery] bool mondayStart = true)
        {
            if (userId <= 0 || careerPathId <= 0) return BadRequest("Invalid ids.");

            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "Europe/Belgrade"); }
            catch { return BadRequest("Invalid timeZone id."); }

            var utcNow     = DateTime.UtcNow;
            var localNow   = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            var localAnchor = (anchorLocalDate ?? localNow.Date).Date;

            int dow = (int)localAnchor.DayOfWeek; // Sunday=0 ... Saturday=6
            int offset = mondayStart
                ? (dow == 0 ? -6 : 1 - dow)   // back to Monday
                : -dow;                        // back to Sunday

            var start = localAnchor.AddDays(offset);
            var dates = Enumerable.Range(0, 7).Select(i => start.AddDays(i).Date).ToArray();

            var minDate = dates.First();
            var maxDate = dates.Last();

            // Load completions for this week
            var completions = await _context.TaskCompletions.AsNoTracking()
                .Where(c => c.UserID == userId
                         && c.CareerPathID == careerPathId
                         && c.CompletedLocalDate != null
                         && c.CompletedLocalDate >= minDate
                         && c.CompletedLocalDate <= maxDate)
                .Select(c => new
                {
                    c.CompletedLocalDate,
                    c.CareerDay
                })
                .ToListAsync();

            // Totals per CareerDay (how many tasks exist in that day)
            var distinctDays = completions.Select(c => c.CareerDay).Distinct().ToArray();
            var totals = await _context.DailyTasks.AsNoTracking()
                .Where(t => t.CareerPathID == careerPathId && distinctDays.Contains(t.Day))
                .GroupBy(t => t.Day)
                .Select(g => new { Day = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Day, x => x.Count);

            var result = new List<object>(7);
            foreach (var d in dates)
            {
                var compsForDate = completions.Where(c => c.CompletedLocalDate == d).ToList();
                var completedCount = compsForDate.Count;

                int totalForDate = compsForDate
                    .Select(c => c.CareerDay)
                    .Distinct()
                    .Sum(cd => totals.TryGetValue(cd, out var cnt) ? cnt : 0);

                result.Add(new
                {
                    localDate = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    completed = completedCount,
                    total = totalForDate
                });
            }

            return Ok(result);
        }

        // ---------------------------
        // NEW: small summary your UI expects (daysCompleted, totalDays)
        // ---------------------------
        [HttpGet("summary")]
        public async Task<IActionResult> Summary(
            [FromQuery] int userId,
            [FromQuery] int careerPathId)
        {
            if (userId <= 0 || careerPathId <= 0) return BadRequest("Invalid ids.");

            // Distinct CareerDays with at least one completion for this path/user
            var daysCompleted = await _context.TaskCompletions.AsNoTracking()
                .Where(c => c.UserID == userId && c.CareerPathID == careerPathId)
                .Select(c => c.CareerDay)
                .Distinct()
                .CountAsync();

            // Total days in this path (max Day in DailyTasks for the path)
            var totalDays = await _context.DailyTasks.AsNoTracking()
                .Where(t => t.CareerPathID == careerPathId)
                .Select(t => (int?)t.Day)
                .DefaultIfEmpty(0)
                .MaxAsync();

            return Ok(new { daysCompleted, totalDays });
        }
    }
}
