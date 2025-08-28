using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DailyTasksController : ControllerBase
    {
        private readonly DailyTaskContext _context;

        public DailyTasksController(DailyTaskContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<DailyTask>>> GetAll()
            => Ok(await _context.DailyTasks.AsNoTracking().ToListAsync());

        [HttpGet("bycareerweek")]
        public async Task<IActionResult> GetWeeklyTasks(
            [FromQuery] int careerPathId,
            [FromQuery] int startDay)
        {
            if (careerPathId <= 0) return BadRequest("careerPathId is required.");
            if (startDay < 0) return BadRequest("startDay must be >= 0.");

            var tasks = await _context.DailyTasks.AsNoTracking()
                .Where(t => t.CareerPathID == careerPathId &&
                            t.Day >= startDay && t.Day < startDay + 7)
                .ToListAsync();

            return Ok(tasks);
        }

        [HttpGet("bycareerandday")]
        public async Task<ActionResult<IEnumerable<DailyTask>>> GetByCareerPathAndDay(
            [FromQuery] int careerPathId,
            [FromQuery] int day)
        {
            if (careerPathId <= 0 || day < 0)
                return BadRequest("Invalid careerPathId or day.");

            var tasks = await _context.DailyTasks.AsNoTracking()
                .Where(t => t.CareerPathID == careerPathId && t.Day == day)
                .ToListAsync();

            return tasks.Any() ? Ok(tasks) : NotFound("No tasks found for that day.");
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<DailyTask>> GetById(int id)
        {
            var task = await _context.DailyTasks.FindAsync(id);
            return task != null ? Ok(task) : NotFound();
        }

        [HttpPost]
        public async Task<ActionResult<DailyTask>> Create(DailyTask task)
        {
            if (task == null) return BadRequest("Missing task.");
            _context.DailyTasks.Add(task);
            await _context.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = task.TaskID }, task);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, DailyTask task)
        {
            if (id != task.TaskID) return BadRequest("Mismatched task ID.");
            _context.Entry(task).State = EntityState.Modified;

            try { await _context.SaveChangesAsync(); return NoContent(); }
            catch (DbUpdateConcurrencyException)
            {
                if (!await _context.DailyTasks.AnyAsync(e => e.TaskID == id))
                    return NotFound();
                throw;
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var task = await _context.DailyTasks.FindAsync(id);
            if (task == null) return NotFound();
            _context.DailyTasks.Remove(task);
            await _context.SaveChangesAsync();
            return NoContent();
        }
    }
}
