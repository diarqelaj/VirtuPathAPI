using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class ChatContext : DbContext
    {
        public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<UserFriend>  UserFriends    { get; set; }
        public DbSet<User>        Users          { get; set; }
        public DbSet<UserBlock>   UserBlocks     { get; set; }
        public DbSet<UserMute>    UserMutes      { get; set; }
        public DbSet<UserPin>     UserPins       { get; set; }
        public DbSet<MessageReaction> MessageReactions { get; set; }
        public DbSet<ChatRequest> ChatRequests   { get; set; }

        // ← NEW: map your vault table here
        public DbSet<CobaltUserKeyVault> ServerKeys { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Keep your existing indexes & relationships
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
                .HasIndex(r => new { r.MessageId, r.UserId })
                .IsUnique();

            modelBuilder.Entity<MessageReaction>()
                .HasOne(r => r.Message)
                .WithMany(m => m.Reactions)
                .HasForeignKey(r => r.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MessageReaction>()
                .HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // ← NEW: ensure EF maps the CobaltUserKeyVault CLR type to your existing table
            modelBuilder
                .Entity<CobaltUserKeyVault>()
                .ToTable("CobaltUserKeyVault");
        }
    }
}
