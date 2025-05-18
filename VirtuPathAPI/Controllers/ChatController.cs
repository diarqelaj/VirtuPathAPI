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

        /* ───────────────────────────── Helpers ───────────────────────────── */

        private int? GetCurrentUserId() => HttpContext.Session.GetInt32("UserID");

        private async Task<bool> AreFriendsAsync(int me, int other) =>
            await _context.UserFriends.AnyAsync(f =>
                ((f.FollowerId == me && f.FollowedId == other) ||
                 (f.FollowedId == me && f.FollowerId == other)) &&
                f.IsAccepted);

        private async Task<int?> UsernameToIdAsync(string username) =>
            await _context.Users
                          .Where(u => u.Username.ToLower() == username.ToLower())
                          .Select(u => (int?)u.UserID)
                          .FirstOrDefaultAsync();

        /* ───────────────────────── ID-based endpoints (unchanged) ───────────────────────── */

        // GET  api/chat/messages/42
        [HttpGet("messages/{withUserId:int}")]
        public async Task<IActionResult> GetMessages(int withUserId)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized("User not logged in.");

            if (!await AreFriendsAsync(me.Value, withUserId))
                return Forbid("You are not friends with this user.");

            var messages = await _context.ChatMessages
                .Where(m =>
                    (m.SenderId == me && m.ReceiverId == withUserId) ||
                    (m.SenderId == withUserId && m.ReceiverId == me))
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            return Ok(messages);
        }

        // POST api/chat/send
        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized("User not logged in.");

            if (!await AreFriendsAsync(me.Value, req.ReceiverId))
                return Forbid("You are not friends with this user.");

            var message = new ChatMessage
            {
                SenderId   = me.Value,
                ReceiverId = req.ReceiverId,
                Message    = req.Message,
                SentAt     = DateTime.UtcNow
            };

            _context.ChatMessages.Add(message);
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }

        /* ───────────────────────── Username-based endpoints (new) ───────────────────────── */

        // GET  api/chat/messages/by-username/jane_doe
        [HttpGet("messages/by-username/{username}")]
        public async Task<IActionResult> GetMessagesByUsername(string username)
        {
            int? friendId = await UsernameToIdAsync(username);
            if (friendId is null) return NotFound("User not found.");

            // Re-use the existing ID-based logic through local redirect
            return await GetMessages(friendId.Value);
        }

        // POST api/chat/send/by-username
        [HttpPost("send/by-username")]
        public async Task<IActionResult> SendMessageByUsername([FromBody] SendMessageByUsernameRequest req)
        {
            int? friendId = await UsernameToIdAsync(req.ReceiverUsername);
            if (friendId is null) return NotFound("User not found.");

            // Re-use the existing ID-based logic
            var idRequest = new SendMessageRequest
            {
                ReceiverId = friendId.Value,
                Message    = req.Message
            };
            return await SendMessage(idRequest);
        }

        /* ───────────────────────── Request DTOs ───────────────────────── */

        public class SendMessageRequest
        {
            public int    ReceiverId { get; set; }
            public string Message    { get; set; } = string.Empty;
        }

        public class SendMessageByUsernameRequest
        {
            public string ReceiverUsername { get; set; } = string.Empty;
            public string Message          { get; set; } = string.Empty;
        }
    }
}
