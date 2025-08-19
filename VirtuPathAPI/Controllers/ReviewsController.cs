using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using VirtuPathAPI.Models;
using VirtuPathAPI.Data; 

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReviewsController : ControllerBase
    {
        private readonly ReviewContext _reviews;
        private readonly UserSubscriptionContext _subs;

        public ReviewsController(ReviewContext reviews, UserSubscriptionContext subs)
        {
            _reviews = reviews;
            _subs = subs;
        }

        // POST: /api/reviews
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Review input)
        {
            if (input == null) return BadRequest("Invalid payload.");
            if (input.Rating < 1 || input.Rating > 5) return BadRequest("Rating must be 1-5.");

            // One review per user per career path
            var already = await _reviews.Reviews.AnyAsync(r =>
                r.UserID == input.UserID && r.CareerPathID == input.CareerPathID);
            if (already)
                return Conflict("You have already submitted a review for this career path.");

            // Must have an active entitlement now
            var now = DateTime.UtcNow;
            var active = await _subs.UserSubscriptions.AnyAsync(s =>
                s.UserID == input.UserID &&
                s.CareerPathID == input.CareerPathID &&
                s.IsActive &&
                !s.IsCanceled &&
                s.StartAt <= now &&
                (s.CurrentPeriodEnd == null || s.CurrentPeriodEnd >= now));

            if (!active)
                return Forbid("Only subscribed users can leave a review for this career path.");

            var review = new Review
            {
                UserID = input.UserID,
                CareerPathID = input.CareerPathID,
                Rating = input.Rating
            };

            _reviews.Reviews.Add(review);
            await _reviews.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = review.ReviewID }, review);
        }

        [HttpGet("exists")]
        public async Task<IActionResult> Exists([FromQuery] int userId, [FromQuery] int careerPathId)
        {
            var exists = await _reviews.Reviews.AnyAsync(r =>
                r.UserID == userId && r.CareerPathID == careerPathId);
            return Ok(new { exists });
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById([FromRoute] int id)
        {
            var review = await _reviews.Reviews.FindAsync(id);
            if (review == null) return NotFound();
            return Ok(review);
        }

        [HttpGet("by-career/{careerPathId:int}")]
        public async Task<IActionResult> GetByCareer([FromRoute] int careerPathId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var query = _reviews.Reviews
                .Where(r => r.CareerPathID == careerPathId)
                .OrderByDescending(r => r.ReviewID);

            var total = await query.CountAsync();
            var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("average/{careerPathId:int}")]
        public async Task<IActionResult> GetAverage([FromRoute] int careerPathId)
        {
            var any = await _reviews.Reviews.AnyAsync(r => r.CareerPathID == careerPathId);
            if (!any) return Ok(new { careerPathId, average = 0.0, count = 0 });

            var avg = await _reviews.Reviews
                .Where(r => r.CareerPathID == careerPathId)
                .AverageAsync(r => r.Rating);

            var count = await _reviews.Reviews.CountAsync(r => r.CareerPathID == careerPathId);

            return Ok(new { careerPathId, average = Math.Round(avg, 2), count });
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var reviews = await _reviews.Reviews.ToListAsync();
            return Ok(reviews);
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete([FromRoute] int id)
        {
            var review = await _reviews.Reviews.FindAsync(id);
            if (review == null) return NotFound();

            _reviews.Reviews.Remove(review);
            await _reviews.SaveChangesAsync();
            return NoContent();
        }
    }
}
