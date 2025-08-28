using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

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
        /// Body must include: UserID, TaskID, CareerPathID, CareerDay, CompletionDate
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<TaskCompletion>> CreateTaskCompletion([FromBody] TaskCompletion completion)
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

            // Save the new completion
            _context.TaskCompletions.Add(completion);
            await _context.SaveChangesAsync();

            // Non-blocking: Update / upsert monthly performance review for this *careerPath*
            try
            {
                int month = completion.CompletionDate.Month;
                int year  = completion.CompletionDate.Year;

                // Assigned tasks in this path (for naive score). If you'd rather use “assigned this month/day”, adjust here.
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
    }
}
