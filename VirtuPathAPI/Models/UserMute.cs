namespace VirtuPathAPI.Models
{
    public class UserMute
    {
        public int Id { get; set; }
        public int MuterId { get; set; }
        public int MutedId { get; set; }
        public DateTime MutedAt { get; set; } = DateTime.UtcNow;
    }
}   
