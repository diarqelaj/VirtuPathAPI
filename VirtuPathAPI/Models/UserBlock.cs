namespace VirtuPathAPI.Models
{
    public class UserBlock
    {
        public int Id { get; set; }
        public int BlockerId { get; set; }
        public int BlockedId { get; set; }
        public DateTime BlockedAt { get; set; } = DateTime.UtcNow;
    }
}   
