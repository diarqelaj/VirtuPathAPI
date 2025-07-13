using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using VirtuPathAPI.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ChatContext _context;
        private readonly IHubContext<ChatHub> _hub; 

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
    if (me is null) return Unauthorized();
    if (!await AreFriendsAsync(me.Value, withUserId)) return Forbid();

    var rows = await _context.ChatMessages
        .Where(m =>
            (m.SenderId == me && m.ReceiverId == withUserId && !m.IsDeletedForSender) ||
            (m.SenderId == withUserId && m.ReceiverId == me && !m.IsDeletedForReceiver))
        .OrderBy(m => m.SentAt)
       .Select(m => new {
    id               = m.Id,
    senderId         = m.SenderId,
    receiverId       = m.ReceiverId,

    // ratchet header
    dhPubB64         = m.DhPubB64,
    pn               = m.PN,
    n                = m.N,

    // ciphertext + IV + tag
    ivB64            = m.Iv,           // <- add this line
    ciphertextB64    = m.Message,
    tagB64           = m.Tag,

    // rest of your metadata
    replyToMessageId = m.ReplyToMessageId,
    sentAt           = m.SentAt,
    isEdited         = m.IsEdited,
    isDeletedForSender   = m.IsDeletedForSender,
    isDeletedForReceiver = m.IsDeletedForReceiver,
    isDelivered      = m.IsDelivered,
    deliveredAt      = m.DeliveredAt,
    isRead           = m.IsRead,
    readAt           = m.ReadAt


})
        .ToListAsync();

    return Ok(rows);
}




      [HttpPost("send")]
public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
{
    int? me = GetCurrentUserId();
    if (me is null) return Unauthorized();
    if (!await AreFriendsAsync(me.Value, req.ReceiverId)) return Forbid();

    // 1) Save the encrypted message
    var msg = new ChatMessage
    {
        SenderId          = me.Value,
        ReceiverId        = req.ReceiverId,

        // ▸ ratchet header
        DhPubB64          = req.DhPubB64.Trim(),
        PN                = req.PN,
        N                 = req.N,

        // ▸ encrypted blob
        Iv                = req.IvB64,
        Message           = req.CiphertextB64,
        Tag               = req.TagB64,

        SentAt            = DateTimeOffset.UtcNow,
        ReplyToMessageId  = req.ReplyToMessageId
    };
    _context.ChatMessages.Add(msg);

    if (!string.IsNullOrWhiteSpace(req.ReactionEmoji))
    {
        _context.MessageReactions.Add(new MessageReaction
        {
            MessageId = msg.Id,
            UserId    = me.Value,
            Emoji     = req.ReactionEmoji
        });
    }

    await _context.SaveChangesAsync();

    // 2) Shape the payload exactly as the TS client expects
    var payload = new
    {
        id                  = msg.Id,
        senderId            = msg.SenderId,
        receiverId          = msg.ReceiverId,

        // ratchet header
        dhPubB64            = msg.DhPubB64,
        pn                  = msg.PN,
        n                   = msg.N,

        // encrypted blob
        ivB64               = msg.Iv,
        ciphertextB64       = msg.Message,
        tagB64              = msg.Tag,

        // metadata
        sentAt              = msg.SentAt,
        replyToMessageId    = msg.ReplyToMessageId,
        isEdited            = msg.IsEdited,
        isDeletedForSender  = msg.IsDeletedForSender,
        isDeletedForReceiver= msg.IsDeletedForReceiver,
        isDelivered         = msg.IsDelivered,
        deliveredAt         = msg.DeliveredAt,
        isRead              = msg.IsRead,
        readAt              = msg.ReadAt
    };

    // 3) Broadcast to both sender and receiver over SignalR
    //    (assumes you set Connection.UserIdentifier = userID.ToString())
    await _hub.Clients.User(req.ReceiverId.ToString())
             .SendAsync("ReceiveEncryptedMessage", payload);

    await _hub.Clients.User(me.Value.ToString())
             .SendAsync("ReceiveEncryptedMessage", payload);

    // 4) Return new message ID
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

    var idReq = new SendMessageRequest
    {
        ReceiverId        = friendId.Value,
        DhPubB64          = req.DhPubB64,
        PN                = req.PN,
        N                 = req.N,
        IvB64             = req.IvB64,
        CiphertextB64     = req.CiphertextB64,
        TagB64            = req.TagB64,
        ReplyToMessageId  = req.ReplyToMessageId
    };
    return await SendMessage(idReq);
}
 public sealed class SendMessageRequest
{
    public int    ReceiverId  { get; set; }

    // ratchet header
    public string DhPubB64    { get; set; } = "";
    public int    PN          { get; set; }
    public int    N           { get; set; }

    // cipher-blob
    public string IvB64        { get; set; } = "";
    public string CiphertextB64{ get; set; } = "";
    public string TagB64       { get; set; } = "";

    public int?   ReplyToMessageId { get; set; }

    // 👇 stays optional
    public string? ReactionEmoji   { get; set; }
}

   public sealed class SendMessageByUsernameRequest
{
    public string ReceiverUsername { get; set; } = "";

    // header + blob
    public string DhPubB64     { get; set; } = "";
    public int    PN           { get; set; }
    public int    N            { get; set; }

    public string IvB64        { get; set; } = "";
    public string CiphertextB64{ get; set; } = "";
    public string TagB64       { get; set; } = "";

    public int?   ReplyToMessageId { get; set; }
}
    }
}
