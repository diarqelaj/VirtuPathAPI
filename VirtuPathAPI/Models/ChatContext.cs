using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class ChatContext : DbContext
    {
        public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<UserFriend> UserFriends { get; set; }
        public DbSet<User> Users { get; set; }
    }

}
