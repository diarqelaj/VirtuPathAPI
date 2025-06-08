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

        public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    }
}


