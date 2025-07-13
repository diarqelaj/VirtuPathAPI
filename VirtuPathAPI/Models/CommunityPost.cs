using System;
using System.Text.Json.Serialization;

namespace VirtuPathAPI.Models
{
    public class CommunityPost
    {
        // PK
        public int PostId { get; set; }

        // FK → Users.UserID
        public int UserID { get; set; }

        // EF navigation back to the author
        [JsonIgnore]
        public User? Author { get; set; }

        // Post content
        public string Content { get; set; } = string.Empty;

        public string? ImageUrl { get; set; }      // ← new
        // Timestamp
        public DateTime CreatedAt { get; set; }

        public ICollection<Comment> Comments { get; set; } = new List<Comment>();
        public ICollection<Reaction> Reactions { get; set; } = new List<Reaction>();
    }
}
