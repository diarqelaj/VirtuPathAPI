namespace VirtuPathAPI.Models
{
    public class ChatMessage
    {
        public int Id          { get; set; }

        public int SenderId    { get; set; }
        public int ReceiverId  { get; set; }

        // Ciphertext (base-64)
        public string Message  { get; set; } = null!;

        // AES-GCM pieces
        public string Iv       { get; set; } = null!;   // base-64, 12 bytes
        public string Tag      { get; set; } = null!;   // base-64, 16 bytes  ← NEW

        // Per-user wrapped AES keys
        public string? WrappedKeyForSender   { get; set; }   // base-64, 256 bytes  ← NEW
        public string? WrappedKeyForReceiver { get; set; }   // base-64, 256 bytes  ← NEW

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public bool IsEdited             { get; set; } = false;
        public bool IsDeletedForSender   { get; set; } = false;
        public bool IsDeletedForReceiver { get; set; } = false;

        public int?    ReplyToMessageId  { get; set; }
        public string? ReactionEmoji     { get; set; }

        public User Sender   { get; set; } = null!;
        public User Receiver { get; set; } = null!;

        /// <summary>Has the _recipient_ ever ack’d “delivered”?</summary>
        public bool IsDelivered   { get; set; } = false;

        /// <summary>When they first ack’d “delivered”</summary>
        public DateTime? DeliveredAt { get; set; }

        /// <summary>Has the _recipient_ ever ack’d “read”?</summary>
        public bool IsRead        { get; set; } = false;

        /// <summary>When they first ack’d “read”</summary>
        public DateTime? ReadAt     { get; set; }

        public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    }
}


