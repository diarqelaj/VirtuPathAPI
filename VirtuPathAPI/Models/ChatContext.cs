using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
   public class ChatContext : DbContext
{
    public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

    public DbSet<ChatMessage> ChatMessages { get; set; }
    public DbSet<UserFriend> UserFriends { get; set; }
    public DbSet<User> Users { get; set; }

    // Add these:
    public DbSet<UserBlock> UserBlocks { get; set; }
    public DbSet<UserMute> UserMutes { get; set; }
    public DbSet<UserPin> UserPins { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Optional: prevent duplicate pins/blocks/mutes
        modelBuilder.Entity<UserBlock>()
            .HasIndex(b => new { b.BlockerId, b.BlockedId })
            .IsUnique();

        modelBuilder.Entity<UserMute>()
            .HasIndex(m => new { m.MuterId, m.MutedId })
            .IsUnique();

        modelBuilder.Entity<UserPin>()
            .HasIndex(p => new { p.UserId, p.PinnedUserId })
            .IsUnique();
    }
}


}
