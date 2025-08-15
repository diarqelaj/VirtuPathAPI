using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using VirtuPathAPI.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ChatContext _context;
        private readonly IHubContext<ChatHub> _hub; 

        public ChatController(
            ChatContext context,
            IHubContext<ChatHub> hub
        ) {
            _context = context;
            _hub     = hub;
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
    if (me == null) return Unauthorized();
    if (!await AreFriendsAsync(me.Value, withUserId)) return Forbid();

    var rows = await _context.ChatMessages
        .Where(m =>
            (m.SenderId == me && m.ReceiverId == withUserId && !m.IsDeletedForSender) ||
            (m.SenderId == withUserId && m.ReceiverId == me && !m.IsDeletedForReceiver))
        .OrderBy(m => m.SentAt)
        .Select(m => new
        {
            id = m.Id,
            senderId = m.SenderId,
            receiverId = m.ReceiverId,

            iv = m.Iv,
            message = m.Message,
            tag = m.Tag,

            sentAt = m.SentAt,
            replyToMessageId = m.ReplyToMessageId,
            isEdited = m.IsEdited,
            isDeletedForSender = m.IsDeletedForSender,
            isDeletedForReceiver = m.IsDeletedForReceiver,
            isDelivered = m.IsDelivered,
            deliveredAt = m.DeliveredAt,
            isRead = m.IsRead,
            readAt = m.ReadAt,

            // 👇 include reactions so the client can render on reload
            reactions = _context.MessageReactions
                .Where(r => r.MessageId == m.Id)
                .Select(r => new {
                    userId            = r.UserId,
                    emoji             = r.Emoji,
                    fullName          = r.User.FullName,
                    profilePictureUrl = r.User.ProfilePictureUrl
                })
                .ToList()
        })
        .ToListAsync();

    return Ok(rows);
}
[HttpGet("unread‑counts")]
public async Task<IActionResult> GetUnreadCounts()
{
    var me = GetCurrentUserId();
    if (me == null) return Unauthorized();

    // 1) Pick every message sent *to* me that’s still unread
    // 2) Compute “lastSentId” for this conversation on the fly
    // 3) Filter only those whose Id > lastSentId (i.e. arrived after my last reply)
    // 4) Group and count
    var counts = await _context.ChatMessages
        .Where(m =>
            m.ReceiverId == me.Value &&
            !m.IsRead
        )
        .Select(m => new
        {
            SenderId   = m.SenderId,
            // If I’ve replied, find the max Id I sent back to them; else 0
            LastSentId = _context.ChatMessages
                .Where(x => x.SenderId == me.Value && x.ReceiverId == m.SenderId)
                .Select(x => (int?)x.Id)
                .Max() ?? 0,
            ThisMsgId  = m.Id
        })
        .Where(x => x.ThisMsgId > x.LastSentId)
        .GroupBy(x => x.SenderId)
        .Select(g => new {
            friendId    = g.Key,
            unreadCount = g.Count()
        })
        .ToListAsync();

    return Ok(counts);
}
[HttpPost("mark-read/{withUserId:int}")]
public async Task<IActionResult> MarkConversationRead(int withUserId)
{
    var me = GetCurrentUserId();
    if (me == null) return Unauthorized();

    var now = DateTimeOffset.UtcNow;

    // Fetch into memory
    var toMark = await _context.ChatMessages
        .Where(m =>
            m.SenderId   == withUserId &&
            m.ReceiverId == me.Value   &&
            !m.IsRead
        )
        .ToListAsync();

    // Mark each one read
    foreach (var msg in toMark)
    {
        msg.IsRead = true;
        msg.ReadAt = now;
    }

    // Persist
    await _context.SaveChangesAsync();

    return Ok(new { marked = toMark.Count });
}




[HttpGet("conversations/key/{withUserId:int}")]
public async Task<IActionResult> GetConversationKey(int withUserId)
{
    var me = GetCurrentUserId();
    if (me == null) return Unauthorized();
    if (!await AreFriendsAsync(me.Value, withUserId)) return Forbid();

    var a = Math.Min(me.Value, withUserId);
    var b = Math.Max(me.Value, withUserId);
    var conv = await _context.ChatConversations
                     .SingleOrDefaultAsync(c => c.UserAId == a && c.UserBId == b);

    if (conv == null)
    {
        // first time: create one
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        conv = new ChatConversation {
            UserAId      = a,
            UserBId      = b,
            SymmetricKey = Convert.ToBase64String(keyBytes)
        };
        _context.ChatConversations.Add(conv);
        await _context.SaveChangesAsync();
    }

    return Ok(new { key = conv.SymmetricKey });
}


    [HttpPost("send")]
public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
{
    var me = GetCurrentUserId();
    if (me == null) return Unauthorized();
    if (!await AreFriendsAsync(me.Value, req.ReceiverId)) return Forbid();

    var msg = new ChatMessage {
        SenderId         = me.Value,
        ReceiverId       = req.ReceiverId,
        Iv               = req.IvB64,
        Message          = req.CiphertextB64,
        Tag              = req.TagB64,
        SentAt           = DateTimeOffset.UtcNow,
        ReplyToMessageId = req.ReplyToMessageId
    };

    _context.ChatMessages.Add(msg);
    await _context.SaveChangesAsync();            // 👈 msg.Id is now assigned

    if (!string.IsNullOrWhiteSpace(req.ReactionEmoji))
    {
        _context.MessageReactions.Add(new MessageReaction {
            MessageId = msg.Id,
            UserId    = me.Value,
            Emoji     = req.ReactionEmoji
        });
        await _context.SaveChangesAsync();
    }

    var payload = new {
        id               = msg.Id,
        senderId         = msg.SenderId,
        receiverId       = msg.ReceiverId,
        ivB64            = msg.Iv,
        ciphertextB64    = msg.Message,
        tagB64           = msg.Tag,
        sentAt           = msg.SentAt,
        replyToMessageId = msg.ReplyToMessageId
    };

    await _hub.Clients.User(req.ReceiverId.ToString())
             .SendAsync("ReceiveEncryptedMessage", payload);
    await _hub.Clients.User(me.Value.ToString())
             .SendAsync("ReceiveEncryptedMessage", payload);

    return Ok(new { id = msg.Id });
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
    var friendId = await UsernameToIdAsync(username);
    if (friendId == null) return NotFound("User not found.");
    return await GetMessages(friendId.Value);
}

       [HttpPost("send/by-username")]
public async Task<IActionResult> SendMessageByUsername([FromBody] SendMessageByUsernameRequest req)
{
    var me = GetCurrentUserId();
    if (me == null) return Unauthorized();

    // 1) username → userId
    var friendId = await UsernameToIdAsync(req.ReceiverUsername);
    if (friendId == null) return NotFound("User not found.");

    // 2) are we allowed?
    if (!await AreFriendsAsync(me.Value, friendId.Value)) return Forbid();

    // 3) reuse your ID‑based SendMessage
    var msgReq = new SendMessageRequest {
        ReceiverId       = friendId.Value,
        IvB64            = req.IvB64,
        CiphertextB64    = req.CiphertextB64,
        TagB64           = req.TagB64,
        ReplyToMessageId = req.ReplyToMessageId,
        ReactionEmoji    = req.ReactionEmoji
    };
    return await SendMessage(msgReq);
}

 public sealed class SendMessageRequest
{
    public int    ReceiverId       { get; set; }
    public string IvB64            { get; set; } = "";
    public string CiphertextB64    { get; set; } = "";
    public string TagB64           { get; set; } = "";
    public int?   ReplyToMessageId { get; set; }
    public string? ReactionEmoji   { get; set; }
}

  public sealed class SendMessageByUsernameRequest
{
    public string ReceiverUsername    { get; set; } = "";
    public string IvB64               { get; set; } = "";
    public string CiphertextB64       { get; set; } = "";
    public string TagB64              { get; set; } = "";
    public int?   ReplyToMessageId    { get; set; }
    public string? ReactionEmoji      { get; set; }
}
    }
}
