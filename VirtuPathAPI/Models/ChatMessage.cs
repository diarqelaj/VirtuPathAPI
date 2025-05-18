namespace VirtuPathAPI.Models
{
    public class ChatMessage
    {
        public int Id { get; set; }

        public int SenderId { get; set; }
        public int ReceiverId { get; set; }

        public string Message { get; set; }
        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public bool IsEdited { get; set; } = false;
        public bool IsDeletedForSender { get; set; } = false;
        public bool IsDeletedForReceiver { get; set; } = false;

        public int? ReplyToMessageId { get; set; }
        public string? ReactionEmoji { get; set; }

        public User Sender { get; set; }
        public User Receiver { get; set; }
        public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();

    }
}
