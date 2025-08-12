using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
namespace VirtuPathAPI.Models
{
    public class ChatMessage
    {
                public int    Id         { get; set; }
        public int    SenderId   { get; set; }
        public int    ReceiverId { get; set; }

        // these names must match your DB columns exactly:
        public string Message    { get; set; } = null!;
        public string Iv         { get; set; } = null!;
        public string Tag        { get; set; } = null!;




        // ── metadata ──────────────────────────────────────────────────────
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IsEdited             { get; set; } = false;
        public bool IsDeletedForSender   { get; set; } = false;
        public bool IsDeletedForReceiver { get; set; } = false;
        public int? ReplyToMessageId     { get; set; }

        public bool           IsDelivered  { get; set; } = false;
        public DateTimeOffset? DeliveredAt { get; set; }
        public bool           IsRead       { get; set; } = false;
        public DateTimeOffset? ReadAt      { get; set; }

        // ── navigation props ─────────────────────────────────────────────
        public User Sender   { get; set; } = null!;
        public User Receiver { get; set; } = null!;
        public ICollection<MessageReaction> Reactions { get; set; }
            = new List<MessageReaction>();
    }
}
