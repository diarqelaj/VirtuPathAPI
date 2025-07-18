using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class ChatContext : DbContext
    {
        public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

        // ───── existing tables ──────────────────────────────────────────────
        public DbSet<ChatMessage>      ChatMessages     { get; set; }
        public DbSet<UserFriend>       UserFriends      { get; set; }
        public DbSet<User>             Users            { get; set; }
        public DbSet<UserBlock>        UserBlocks       { get; set; }
        public DbSet<ChatConversation> ChatConversations { get; set; }
        public DbSet<UserMute> UserMutes { get; set; }
        public DbSet<UserPin>          UserPins         { get; set; }
        public DbSet<MessageReaction>  MessageReactions { get; set; }
        public DbSet<ChatRequest>      ChatRequests     { get; set; }
        public DbSet<CobaltUserKeyVault> ServerKeys     { get; set; }

        // ───── new Double-Ratchet tables ────────────────────────────────────
        public DbSet<DR_Session>    DRSessions    { get; set; } = null!;
        public DbSet<DR_SkippedKey> DRSkippedKeys { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
   
            // ─────────── existing indexes / relationships ───────────
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
            modelBuilder.Entity<ChatConversation>()
                        .HasIndex(c => new { c.UserAId, c.UserBId })
                        .IsUnique();

            modelBuilder.Entity<CobaltUserKeyVault>()
                        .ToTable("CobaltUserKeyVault");

            // ─────────── Double-Ratchet mappings ───────────

            // DR_Session -– nothing special: EF creates Id (PK) by default.
            modelBuilder.Entity<DR_Session>()
                        .ToTable("DR_Sessions");

            // DR_SkippedKey – composite PK (SessionId,N) & FK to DR_Session
            modelBuilder.Entity<DR_SkippedKey>()
                        .ToTable("DR_SkippedKeys")
                        .HasKey(k => new { k.SessionId, k.N });

            modelBuilder.Entity<DR_SkippedKey>()
                        .HasOne(k => k.Session)
                        .WithMany()                    // no back-collection needed
                        .HasForeignKey(k => k.SessionId)
                        .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
