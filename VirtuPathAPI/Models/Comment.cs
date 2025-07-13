using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VirtuPathAPI.Models
{
    public class Comment
    {
        public int CommentId { get; set; }
        public int PostId { get; set; }
        public int UserID { get; set; }
        public string Content { get; set; } = string.Empty;
        public int? ParentCommentId { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore] public CommunityPost? Post { get; set; }
        [JsonIgnore] public User? User { get; set; }
        [JsonIgnore] public Comment? ParentComment { get; set; }
        [JsonIgnore] public ICollection<Comment> Replies { get; set; } = new List<Comment>();
    }
}
