namespace VirtuPathAPI.Models
{
    public class UserPin
    {
        public int Id { get; set; }

        public int UserId { get; set; }         // The one who is pinning
        public int PinnedUserId { get; set; }   // The one being pinned

        public DateTime PinnedAt { get; set; } = DateTime.UtcNow;
    }
}