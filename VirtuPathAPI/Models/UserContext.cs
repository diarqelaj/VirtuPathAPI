using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Models
{
    public class UserContext : DbContext
    {
        public UserContext(DbContextOptions<UserContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<UserSubscription> UserSubscriptions { get; set; }
        public DbSet<UserFriend> UserFriends { get; set; }
        public DbSet<PerformanceReview> PerformanceReviews { get; set; }

        // (Optional but recommended naming)
        public DbSet<CobaltUserKeyVault> CobaltUserKeyVaults { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // existing friend links
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

            // NEW: 1–1 User <-> CobaltUserKeyVault
            modelBuilder.Entity<User>()
                .HasOne(u => u.KeyVault)
                .WithOne(v => v.User)
                .HasForeignKey<CobaltUserKeyVault>(v => v.UserId)
                .OnDelete(DeleteBehavior.Cascade); // or Restrict if you don’t want vaults auto-deleted
        }
    }
}
