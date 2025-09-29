using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VirtuPathAPI.Models
{
    public class Feedback
    {
        [BsonId, BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("userId")]
        public string? UserId { get; set; }

        [BsonElement("name")]
        public string? Name { get; set; }

        [BsonElement("email")]
        public string? Email { get; set; }

        [BsonElement("message"), BsonRequired]
        public string Message { get; set; } = default!;

        [BsonElement("rating")]
        public int Rating { get; set; } = 5;

        [BsonElement("createdAt")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("status")]
        public string Status { get; set; } = "new";
    }
}
