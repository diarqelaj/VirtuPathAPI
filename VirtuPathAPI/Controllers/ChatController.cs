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
            var me = GetCurrentUserId();
            if (me is null) return Unauthorized();
            if (!await AreFriendsAsync(me.Value, withUserId)) return Forbid();

            var messages = await _context.ChatMessages
                .Where(m =>
                    (m.SenderId == me && m.ReceiverId == withUserId && !m.IsDeletedForSender) ||
                    (m.SenderId == withUserId && m.ReceiverId == me && !m.IsDeletedForReceiver)
                )
                .OrderBy(m => m.SentAt)
                .Select(m => new {
                    id         = m.Id,
                    senderId   = m.SenderId,
                    receiverId = m.ReceiverId,
                    wrappedKeyForSenderB64   = m.WrappedKeyForSender,
                    wrappedKeyForReceiverB64 = m.WrappedKeyForReceiver,
                    ivB64         = m.Iv,
                    ciphertextB64 = m.Message,
                    tagB64        = m.Tag,
                    replyToMessageId = m.ReplyToMessageId,
                    sentAt           = m.SentAt,
                    isEdited         = m.IsEdited
                })
                .ToListAsync();

            return Ok(messages);
        }



       [HttpPost("send")]
public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
{
    int? me = GetCurrentUserId();
    if (me is null) return Unauthorized();
    if (!await AreFriendsAsync(me.Value, req.ReceiverId)) return Forbid();

    var chat = new ChatMessage
    {
        SenderId             = me.Value,
        ReceiverId           = req.ReceiverId,
        WrappedKeyForSender  = req.WrappedKeyForSenderB64,
        WrappedKeyForReceiver= req.WrappedKeyForReceiverB64,
        Iv                   = req.IvB64,
        Tag                  = req.TagB64,
        Message              = req.CiphertextB64,
        SentAt               = DateTime.UtcNow,
        ReplyToMessageId     = req.ReplyToMessageId
    };
    _context.ChatMessages.Add(chat);
    await _context.SaveChangesAsync();
    return Ok(new { chat.Id });
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
        public class EmojiDto
        {
            public string Emoji { get; set; } = string.Empty;
        }

        
        [HttpPatch("react/{messageId:int}")]
        public async Task<IActionResult> ReactToMessage(int messageId, [FromBody] EmojiDto body)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var message = await _context.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message == null) return NotFound();

            if (message.ReceiverId != me && message.SenderId != me)
                return Forbid();

            var existing = await _context.MessageReactions
                .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == me);

            if (existing != null)
            {
                existing.Emoji = body.Emoji;
            }
            else
            {
                _context.MessageReactions.Add(new MessageReaction
                {
                    MessageId = messageId,
                    UserId = me.Value,
                    Emoji = body.Emoji
                });
            }

            await _context.SaveChangesAsync();

            return Ok(new { reacted = true });
        }
        [HttpGet("react/{messageId:int}")]
        public async Task<IActionResult> GetReactionForMessage(int messageId)
        {
            var reactions = await _context.MessageReactions
                .Where(r => r.MessageId == messageId)
                .Select(r => new
                {
                    r.UserId,
                    r.Emoji,
                    r.User.FullName,
                    r.User.ProfilePictureUrl
                })
                .ToListAsync();

            return Ok(reactions);
        }


        [HttpDelete("react/{messageId:int}")]
        public async Task<IActionResult> RemoveReaction(int messageId)
        {
            int? me = GetCurrentUserId();
            if (me is null) return Unauthorized();

            var reaction = await _context.MessageReactions
                .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == me);

            if (reaction == null) return NotFound();

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
public async Task<IActionResult> SendMessageByUsername(
    [FromBody] SendMessageByUsernameRequest req)
{
    int? friendId = await UsernameToIdAsync(req.ReceiverUsername);
    if (friendId is null) return NotFound("User not found.");

    var idRequest = new SendMessageRequest
    {
        ReceiverId            = friendId.Value,

        // pass the encrypted blob straight through
        WrappedKeyForSenderB64   = req.WrappedKeyForSenderB64,
        WrappedKeyForReceiverB64 = req.WrappedKeyForReceiverB64,
        IvB64                    = req.IvB64,
        CiphertextB64            = req.CiphertextB64,
        TagB64                   = req.TagB64,

        // optional extras
        ReplyToMessageId      = req.ReplyToMessageId,
        ReactionEmoji         = req.ReactionEmoji
    };

    return await SendMessage(idRequest);
}
      public class SendMessageRequest
{
    public int    ReceiverId               { get; set; }

    // ── encrypted payload ───────────────
    public string WrappedKeyForSenderB64   { get; set; } = "";
    public string WrappedKeyForReceiverB64 { get; set; } = "";
    public string IvB64                    { get; set; } = "";
    public string CiphertextB64            { get; set; } = "";
    public string TagB64                   { get; set; } = "";

    // ── extras (optional) ───────────────
    public int?   ReplyToMessageId         { get; set; }
    public string? ReactionEmoji           { get; set; }   // 👍 keep
}

        public class SendMessageByUsernameRequest
{
    public string ReceiverUsername          { get; set; } = string.Empty;

    // ── encrypted payload ────────────────────────────
    public string WrappedKeyForSenderB64    { get; set; } = "";
    public string WrappedKeyForReceiverB64  { get; set; } = "";
    public string IvB64                     { get; set; } = "";
    public string CiphertextB64             { get; set; } = "";
    public string TagB64                    { get; set; } = "";

    // ── extras (optional) ────────────────────────────
    public int?   ReplyToMessageId          { get; set; }
    public string? ReactionEmoji            { get; set; }
}
    }
}
