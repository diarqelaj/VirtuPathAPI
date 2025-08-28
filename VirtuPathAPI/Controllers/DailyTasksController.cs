using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using System.Linq;
using System.Threading.Tasks;
using TimeZoneConverter;

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
        {
            return Ok(await _context.DailyTasks.ToListAsync());
        }

        [HttpGet("bycareerweek")]
        public async Task<IActionResult> GetWeeklyTasks([FromQuery] int careerPathId, [FromQuery] int startDay)
        {
            if (careerPathId <= 0) return BadRequest("careerPathId is required.");
            if (startDay < 0) return BadRequest("startDay must be >= 0.");

            var tasks = await _context.DailyTasks
                .Where(t => t.CareerPathID == careerPathId && t.Day >= startDay && t.Day < startDay + 7)
                .ToListAsync();

            return Ok(tasks);
        }

        [HttpGet("bycareerandday")]
        public async Task<ActionResult<IEnumerable<DailyTask>>> GetByCareerPathAndDay([FromQuery] int careerPathId, [FromQuery] int day)
        {
            if (careerPathId <= 0 || day < 0)
                return BadRequest("Invalid careerPathId or day.");

            var tasks = await _context.DailyTasks
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

            try
            {
                await _context.SaveChangesAsync();
                return NoContent();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!await _context.DailyTasks.AnyAsync(e => e.TaskID == id))
                    return NotFound();
                throw;
            }
        }
      [HttpGet("today")]
public async Task<IActionResult> GetTodayTasks(
    [FromQuery] int? careerPathId,
    [FromHeader(Name = "X-Timezone")] string? tzHeader,
    [FromQuery(Name = "tz")] string? tzQuery)
{
    if (careerPathId is null || careerPathId <= 0)
        return BadRequest("careerPathId is required.");

    // Header first, then query fallback
    var tzRaw = !string.IsNullOrWhiteSpace(tzHeader) ? tzHeader : tzQuery;
    if (string.IsNullOrWhiteSpace(tzRaw))
        return BadRequest("Missing timezone. Send X-Timezone header or ?tz=Area/City.");

    // Handle IANA/Windows on any OS
    TimeZoneInfo userTz;
    try
    {
        // If it already matches, this succeeds on the current OS
        userTz = TimeZoneInfo.FindSystemTimeZoneById(tzRaw);
    }
    catch
    {
        try
        {
            // Convert IANA<->Windows as needed
            if (OperatingSystem.IsWindows())
            {
                // Browser gave IANA (e.g., "Europe/Bucharest")
                var windowsId = TZConvert.IanaToWindows(tzRaw);
                userTz = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            else
            {
                // If somehow a Windows ID came in on Linux
                var ianaId = TZConvert.WindowsToIana(tzRaw);
                userTz = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            }
        }
        catch
        {
            return BadRequest("Invalid timezone id.");
        }
    }

    var userId = HttpContext.Session.GetInt32("UserID");
    if (userId is null) return Unauthorized("User not logged in.");

    var cpId = careerPathId.Value;
    var todayLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, userTz).Date;

    // ensure DbSet<UserCareerProgress> exists in context
    var prog = await _context.UserCareerProgresses
        .SingleOrDefaultAsync(p => p.UserID == userId && p.CareerPathID == cpId);

    if (prog == null)
    {
        prog = new UserCareerProgress
        {
            UserID = userId.Value,
            CareerPathID = cpId,
            CurrentDay = 1,
            LastTaskDate = null
        };
        _context.UserCareerProgresses.Add(prog);
        await _context.SaveChangesAsync();
    }

    if (prog.LastTaskDate == null || prog.LastTaskDate.Value.Date < todayLocal)
    {
        var prevDay = prog.CurrentDay;

        var prevTaskIds = await _context.DailyTasks.AsNoTracking()
            .Where(t => t.CareerPathID == cpId && t.Day == prevDay)
            .Select(t => t.TaskID)
            .ToListAsync();

        var completedIds = await _context.TaskCompletions.AsNoTracking()
            .Where(c => c.UserID == userId && c.CareerPathID == cpId && c.CareerDay == prevDay)
            .Select(c => c.TaskID)
            .ToListAsync();

        var required = (int)Math.Ceiling(prevTaskIds.Count * 0.5);
        var done = completedIds.Count(id => prevTaskIds.Contains(id));

        if (prevTaskIds.Count > 0 && done >= required) prog.CurrentDay += 1;

        prog.LastTaskDate = todayLocal;
        await _context.SaveChangesAsync();
    }

    var todayTasks = await _context.DailyTasks.AsNoTracking()
        .Where(t => t.CareerPathID == cpId && t.Day == prog.CurrentDay)
        .Select(t => new { t.TaskID, t.TaskDescription, t.Day })
        .ToListAsync();

    return Ok(new { currentDay = prog.CurrentDay, tasks = todayTasks });
}


        /// <summary>
        /// Returns the user's "today" tasks for the specified careerPathId,
        /// and handles per-path day rollover (50% rule) based on the user's local day.
        /// </summary>
        /// <returns>{ currentDay, tasks }</returns>
      
        [HttpPost("submit-day")]
        public async Task<IActionResult> SubmitCurrentDay([FromQuery] int careerPathId)
        {
            if (careerPathId <= 0) return BadRequest("careerPathId is required.");

            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized("User not logged in.");

            var progress = await _context.UserCareerProgresses
                .SingleOrDefaultAsync(p => p.UserID == userId.Value && p.CareerPathID == careerPathId);

            if (progress == null) return BadRequest("No progress for this career path.");

            string timeZoneId = Request.Headers["X-Timezone"];
            if (string.IsNullOrWhiteSpace(timeZoneId)) return BadRequest("Missing timezone.");
            TimeZoneInfo userTimeZone;
            try { userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch { return BadRequest("Invalid timezone."); }

            var nowUtc = DateTime.UtcNow;
            var todayLocal = TimeZoneInfo.ConvertTime(nowUtc, userTimeZone).Date;
            var lastTaskDate = progress.LastTaskDate?.Date ?? DateOnly.MinValue.ToDateTime(TimeOnly.MinValue).Date;

            if (lastTaskDate >= todayLocal)
                return BadRequest("You’ve already submitted for today. Come back tomorrow.");

            var todayTaskIds = await _context.DailyTasks
                .Where(t => t.CareerPathID == careerPathId && t.Day == progress.CurrentDay)
                .Select(t => t.TaskID)
                .ToListAsync();

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId.Value
                          && tc.CareerPathID == careerPathId
                          && tc.CareerDay == progress.CurrentDay)
                .Select(tc => tc.TaskID)
                .ToListAsync();

            int completedCount = completedTaskIds.Count(id => todayTaskIds.Contains(id));
            int requiredToPass = (int)Math.Ceiling(todayTaskIds.Count * 0.5);

            if (todayTaskIds.Count == 0)
                return BadRequest("No tasks found for today.");
            if (completedCount < requiredToPass)
                return BadRequest($"You must complete at least {requiredToPass} of {todayTaskIds.Count} tasks.");

            // Do not advance here; it will advance after local midnight in GetTodayTasks.
            return Ok(new
            {
                message = "✅ Tasks completed. New day will start automatically after midnight.",
                tasksCompleted = completedCount,
                tasksRequired = requiredToPass
            });
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
