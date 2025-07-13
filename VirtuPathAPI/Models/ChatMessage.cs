namespace VirtuPathAPI.Models
{
    public class ChatMessage
    {
        public int Id         { get; set; }
        public int SenderId   { get; set; }
        public int ReceiverId { get; set; }

        // ── ciphertext ───────────────────────────────────────
        public string Message { get; set; } = null!;   // Base-64
        public string Iv      { get; set; } = null!;   // Base-64  (12 bytes GCM nonce)
        public string Tag     { get; set; } = null!;   // Base-64  (16 bytes GCM tag)

        // ── NEW ratchet header ───────────────────────────────
        public string DhPubB64 { get; set; } = "";     // sender’s DH (base-64)
        public int    PN       { get; set; }           // msgs on previous sending chain
        public int    N        { get; set; }           // msgs on *this* sending chain

        // ── meta ─────────────────────────────────────────────
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IsEdited             { get; set; } = false;
        public bool IsDeletedForSender   { get; set; } = false;
        public bool IsDeletedForReceiver { get; set; } = false;

        public int? ReplyToMessageId { get; set; }

        public bool           IsDelivered  { get; set; } = false;
        public DateTimeOffset? DeliveredAt { get; set; }
        public bool           IsRead       { get; set; } = false;
        public DateTimeOffset? ReadAt      { get; set; }

        public User Sender   { get; set; } = null!;
        public User Receiver { get; set; } = null!;

        public ICollection<MessageReaction> Reactions { get; set; }
            = new List<MessageReaction>();
    }
}
