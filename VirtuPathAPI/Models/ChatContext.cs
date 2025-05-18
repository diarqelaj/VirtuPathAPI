using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class ChatContext : DbContext
    {
        public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<UserFriend> UserFriends { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<UserBlock> UserBlocks { get; set; }
        public DbSet<UserMute> UserMutes { get; set; }
        public DbSet<UserPin> UserPins { get; set; }
        public DbSet<MessageReaction> MessageReactions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Prevent duplicates
            modelBuilder.Entity<UserBlock>()
                .HasIndex(b => new { b.BlockerId, b.BlockedId })
                .IsUnique();

            modelBuilder.Entity<UserMute>()
                .HasIndex(m => new { m.MuterId, m.MutedId })
                .IsUnique();

            modelBuilder.Entity<UserPin>()
                .HasIndex(p => new { p.UserId, p.PinnedUserId })
                .IsUnique();

            modelBuilder.Entity<MessageReaction>()
                .HasIndex(r => new { r.MessageId, r.UserId }) // one reaction per user per message
                .IsUnique();

            modelBuilder.Entity<MessageReaction>()
                .HasOne(r => r.Message)
                .WithMany(m => m.Reactions)
                .HasForeignKey(r => r.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MessageReaction>()
                .HasOne(r => r.User)
                .WithMany() // or define a navigation if needed
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
