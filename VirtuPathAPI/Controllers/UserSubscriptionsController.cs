using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserSubscriptionsController : ControllerBase
    {
        private readonly UserSubscriptionContext _subs;
        private readonly UserContext _users;

        public UserSubscriptionsController(UserSubscriptionContext subs, UserContext users)
        {
            _subs  = subs;
            _users = users;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserSubscription>>> GetUserSubscriptions()
        {
            return await _subs.UserSubscriptions.ToListAsync();
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<UserSubscription>> GetUserSubscription(int id)
        {
            var subscription = await _subs.UserSubscriptions.FindAsync(id);
            if (subscription == null) return NotFound();
            return subscription;
        }

        [HttpPost]
        public async Task<ActionResult<UserSubscription>> CreateUserSubscription([FromBody] UserSubscription subscription)
        {
            // Ensure required fields
            if (subscription.UserID <= 0 || subscription.CareerPathID <= 0)
                return BadRequest("UserID and CareerPathID are required.");

            // Default StartAt if not provided
            if (subscription.StartAt == default)
                subscription.StartAt = DateTime.UtcNow;

            // If CurrentPeriodEnd not provided, derive from Billing
            if (subscription.CurrentPeriodEnd == null && !string.IsNullOrWhiteSpace(subscription.Billing))
            {
                var billing = subscription.Billing.Trim().ToLowerInvariant();
                if (billing is "monthly")
                    subscription.CurrentPeriodEnd = subscription.StartAt.AddDays(30);
                else if (billing is "yearly")
                    subscription.CurrentPeriodEnd = subscription.StartAt.AddDays(365);
                // "one_time" or unknown → leave null (lifetime / external control)
            }

            _subs.UserSubscriptions.Add(subscription);
            await _subs.SaveChangesAsync();

            // Also set user's active career and kick off day 1 if needed
            var user = await _users.Users.FirstOrDefaultAsync(u => u.UserID == subscription.UserID);
            if (user != null)
            {
                user.CareerPathID = subscription.CareerPathID;
                if (user.CurrentDay <= 0) user.CurrentDay = 1;
                user.LastTaskDate = null;
                user.LastKnownIP  = HttpContext.Connection.RemoteIpAddress?.ToString();
                user.LastActiveAt = DateTime.UtcNow;
                await _users.SaveChangesAsync();
            }

            return CreatedAtAction(nameof(GetUserSubscription), new { id = subscription.Id }, subscription);
        }

        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateUserSubscription(int id, [FromBody] UserSubscription subscription)
        {
            if (id != subscription.Id) return BadRequest("Mismatched id.");

            // We track only allowed updates; attach and mark modified
            _subs.Entry(subscription).State = EntityState.Modified;

            try
            {
                await _subs.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                var exists = await _subs.UserSubscriptions.AnyAsync(s => s.Id == id);
                if (!exists) return NotFound();
                throw;
            }

            return NoContent();
        }

        // kept route name but now returns StartAt (renamed from StartDate)
        [HttpGet("startdate")]
        public async Task<ActionResult<DateTime>> GetStartDate([FromQuery] int userId, [FromQuery] int careerPathId)
        {
            var subscription = await _subs.UserSubscriptions
                .Where(x => x.UserID == userId && x.CareerPathID == careerPathId)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            if (subscription == null)
                return NotFound("No subscription found");

            return Ok(subscription.StartAt);
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteUserSubscription(int id)
        {
            var subscription = await _subs.UserSubscriptions.FindAsync(id);
            if (subscription == null) return NotFound();

            _subs.UserSubscriptions.Remove(subscription);
            await _subs.SaveChangesAsync();
            return NoContent();
        }
    }
}
