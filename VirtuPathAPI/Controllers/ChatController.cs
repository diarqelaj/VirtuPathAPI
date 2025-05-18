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

        [HttpGet("messages/{withUserId:int}")]
        public async Task<IActionResult> GetMessages(int withUserId)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized("User not logged in.");

            if (!await AreFriendsAsync(me.Value, withUserId))
                return Forbid("You are not friends with this user.");

            var messages = await _context.ChatMessages
                .Where(m =>
                    ((m.SenderId == me && m.ReceiverId == withUserId && !m.IsDeletedForSender) ||
                     (m.SenderId == withUserId && m.ReceiverId == me && !m.IsDeletedForReceiver)))
                .OrderBy(m => m.SentAt)
                .ToListAsync();

            return Ok(messages);
        }

        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized("User not logged in.");

            if (!await AreFriendsAsync(me.Value, req.ReceiverId))
                return Forbid("You are not friends with this user.");

            var message = new ChatMessage
            {
                SenderId = me.Value,
                ReceiverId = req.ReceiverId,
                Message = req.Message,
                SentAt = DateTime.UtcNow,
                ReplyToMessageId = req.ReplyToMessageId,
                ReactionEmoji = req.ReactionEmoji
            };

            _context.ChatMessages.Add(message);
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }

        [HttpPost("delete/{messageId:int}/sender")]
        public async Task<IActionResult> DeleteForSender(int messageId)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var message = await _context.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message == null || message.SenderId != me.Value) return NotFound();

            message.IsDeletedForSender = true;
            await _context.SaveChangesAsync();

            return Ok(new { deleted = true });
        }

        [HttpPost("delete-for-everyone/{messageId:int}")]
        public async Task<IActionResult> DeleteForEveryone(int messageId)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var message = await _context.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message == null || message.SenderId != me.Value) return NotFound();

            message.IsDeletedForSender = true;
            message.IsDeletedForReceiver = true;
            await _context.SaveChangesAsync();

            return Ok(new { deletedForAll = true });
        }

        public class EditMessageRequest
        {
            public string NewMessage { get; set; } = string.Empty;
        }

        [HttpPut("edit/{messageId:int}")]
        public async Task<IActionResult> EditMessage(int messageId, [FromBody] EditMessageRequest body)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var message = await _context.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message == null || message.SenderId != me.Value) return NotFound();

            message.Message = body.NewMessage;
            message.IsEdited = true;
            await _context.SaveChangesAsync();

            return Ok(new { edited = true });
        }

        [HttpPatch("react/{messageId:int}")]
        public async Task<IActionResult> ReactToMessage(int messageId, [FromBody] string emoji)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var message = await _context.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message == null) return NotFound();

            if (message.ReceiverId != me && message.SenderId != me)
                return Forbid();

            message.ReactionEmoji = emoji;
            await _context.SaveChangesAsync();

            return Ok(new { reacted = true });
        }
        [HttpDelete("react/{messageId:int}")]
        public async Task<IActionResult> RemoveReaction(int messageId)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var reaction = await _context.MessageReactions
                .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == me.Value);

            if (reaction == null) return NotFound("No reaction to remove.");

            _context.MessageReactions.Remove(reaction);
            await _context.SaveChangesAsync();

            return Ok(new { removed = true });
        }


        [HttpGet("messages/by-username/{username}")]
        public async Task<IActionResult> GetMessagesByUsername(string username)
        {
            int? friendId = await UsernameToIdAsync(username);
            if (friendId is null) return NotFound("User not found.");
            return await GetMessages(friendId.Value);
        }

        [HttpPost("send/by-username")]
        public async Task<IActionResult> SendMessageByUsername([FromBody] SendMessageByUsernameRequest req)
        {
            int? friendId = await UsernameToIdAsync(req.ReceiverUsername);
            if (friendId is null) return NotFound("User not found.");

            var idRequest = new SendMessageRequest
            {
                ReceiverId = friendId.Value,
                Message = req.Message,
                ReplyToMessageId = req.ReplyToMessageId,
                ReactionEmoji = req.ReactionEmoji
            };
            return await SendMessage(idRequest);
        }

        public class SendMessageRequest
        {
            public int ReceiverId { get; set; }
            public string Message { get; set; } = string.Empty;
            public int? ReplyToMessageId { get; set; }
            public string? ReactionEmoji { get; set; }
        }

        public class SendMessageByUsernameRequest
        {
            public string ReceiverUsername { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public int? ReplyToMessageId { get; set; }
            public string? ReactionEmoji { get; set; }
        }
    }
}
