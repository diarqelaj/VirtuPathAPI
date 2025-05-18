using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ChatContext _context;

        public ChatController(ChatContext context)
        {
            _context = context;
        }

        // ✅ Get messages with a friend (auto uses session for logged-in user)
        [HttpGet("messages/{withUserId}")]
        public async Task<IActionResult> GetMessages(int withUserId)
        {
            int? me = HttpContext.Session.GetInt32("UserID");
            if (me == null)
                return Unauthorized("User not logged in.");

            var areFriends = await _context.UserFriends.AnyAsync(f =>
                ((f.FollowerId == me && f.FollowedId == withUserId) ||
                 (f.FollowedId == me && f.FollowerId == withUserId)) &&
                f.IsAccepted);

            if (!areFriends)
                return Forbid("You are not friends with this user.");

            var messages = await _context.ChatMessages
                .Where(m => (m.SenderId == me && m.ReceiverId == withUserId) ||
                            (m.SenderId == withUserId && m.ReceiverId == me))
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            return Ok(messages);
        }

        // ✅ Send message to friend (auto uses session for logged-in user)
        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
        {
            int? me = HttpContext.Session.GetInt32("UserID");
            if (me == null)
                return Unauthorized("User not logged in.");

            var areFriends = await _context.UserFriends.AnyAsync(f =>
                ((f.FollowerId == me && f.FollowedId == req.ReceiverId) ||
                 (f.FollowedId == me && f.FollowerId == req.ReceiverId)) &&
                f.IsAccepted);

            if (!areFriends)
                return Forbid("You are not friends with this user.");

            var message = new ChatMessage
            {
                SenderId = me.Value,
                ReceiverId = req.ReceiverId,
                Message = req.Message,
                SentAt = DateTime.UtcNow
            };

            _context.ChatMessages.Add(message);
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }

        public class SendMessageRequest
        {
            public int ReceiverId { get; set; }
            public string Message { get; set; }
        }
    }
}
