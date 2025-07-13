using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace VirtuPathAPI.Models
{
    public class CommentReaction
    {
        public int CommentReactionId { get; set; }
        public int CommentId { get; set; }
        public int UserID { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore] public Comment? Comment { get; set; }
        [JsonIgnore] public User? User { get; set; }
    }

}
