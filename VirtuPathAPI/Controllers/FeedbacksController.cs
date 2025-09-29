using Microsoft.AspNetCore.Mvc;
using VirtuPathAPI.Data;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedbacksController : ControllerBase
    {
        private readonly IFeedbackRepository _repo;
        public FeedbacksController(IFeedbackRepository repo) => _repo = repo;

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Feedback f)
        {
            if (string.IsNullOrWhiteSpace(f.Message))
                return BadRequest("Message is required.");

            var id = await _repo.CreateAsync(f);
            return CreatedAtAction(nameof(GetById), new { id }, new { id });
        }

        [HttpGet]
        public Task<List<Feedback>> GetAll([FromQuery] int page = 1, [FromQuery] int size = 20)
        {
            var skip = (page - 1) * size;
            return _repo.GetAllAsync(skip, size);
        }

        [HttpGet("{id}")]
        public Task<Feedback?> GetById(string id) => _repo.GetByIdAsync(id);

        [HttpGet("by-user/{userId}")]
        public Task<List<Feedback>> ByUser(string userId, [FromQuery] int page = 1, [FromQuery] int size = 20)
        {
            var skip = (page - 1) * size;
            return _repo.GetByUserAsync(userId, skip, size);
        }

        [HttpPatch("{id}/status/{status}")]
        public async Task<IActionResult> UpdateStatus(string id, string status)
        {
            var ok = await _repo.UpdateStatusAsync(id, status);
            return ok ? NoContent() : NotFound();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var n = await _repo.DeleteAsync(id);
            return n == 1 ? NoContent() : NotFound();
        }
    }
}
