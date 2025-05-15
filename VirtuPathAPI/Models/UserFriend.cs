using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    public class UserFriend
    {
        public int Id { get; set; }
        public int FollowerId { get; set; }     // who followed
        public int FollowedId { get; set; }     // who was followed
        public bool IsAccepted { get; set; } = false;
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

        // ✅ Navigation Properties
        [ForeignKey("FollowerId")]
        public User Follower { get; set; }

        [ForeignKey("FollowedId")]
        public User Followed { get; set; }
    }
}
