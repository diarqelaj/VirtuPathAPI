using System;
using System.Text.Json.Serialization;

namespace VirtuPathAPI.Models
{
    public enum ReactionType
    {
        Like = 1,
        Dislike = -1
    }

    public class Reaction
    {
        public int ReactionId { get; set; }
        public int PostId { get; set; }
        public int UserID { get; set; }
        public ReactionType Type { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore] public CommunityPost? Post { get; set; }
        [JsonIgnore] public User? User { get; set; }
    }
}
