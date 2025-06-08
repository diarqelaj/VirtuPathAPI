// File: Models/UserContext.cs
using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class UserContext : DbContext
    {
        public UserContext(DbContextOptions<UserContext> options)
            : base(options)
        { }

        public DbSet<User> Users { get; set; }
        public DbSet<UserSubscription> UserSubscriptions { get; set; }
        public DbSet<UserFriend> UserFriends { get; set; }

        // ← Make sure this line exists (register the vault DbSet)
        public DbSet<CobaltUserKeyVault> CobaltUserKeyVault { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── 1) Friendship setup (unchanged from before) ─────────────────────
            modelBuilder.Entity<UserFriend>()
                .HasOne(f => f.Follower)
                .WithMany()
                .HasForeignKey(f => f.FollowerId)
                .OnDelete(DeleteBehavior.NoAction);

            modelBuilder.Entity<UserFriend>()
                .HasOne(f => f.Followed)
                .WithMany()
                .HasForeignKey(f => f.FollowedId)
                .OnDelete(DeleteBehavior.NoAction);

            // ── 2) Define the vault’s primary key (VaultId) and the 1:1 with User ───
           modelBuilder.Entity<CobaltUserKeyVault>()
             .HasKey(v => v.UserId);
            modelBuilder.Entity<CobaltUserKeyVault>()
                .HasOne(v => v.User)
                .WithOne(u => u.KeyVault)            // User.KeyVault navigation
                .HasForeignKey<CobaltUserKeyVault>(v => v.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // (No need for “HasKey(v => v.UserId)” here—VaultId is the key.)
        }
    }
}
