namespace VirtuPathAPI.Models
{
    public class MessageReaction
    {
        public int Id { get; set; }
        public int MessageId { get; set; }
        public int UserId { get; set; }
        public string Emoji { get; set; } = string.Empty;

        public ChatMessage Message { get; set; } = null!;
        public User User { get; set; } = null!;
    }
 }
