using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class UserContext : DbContext
    {
        public UserContext(DbContextOptions<UserContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<UserSubscription> UserSubscriptions { get; set; }
        public DbSet<UserFriend> UserFriends { get; set; }
        public DbSet<PerformanceReview> PerformanceReviews { get; set; }

        // Keep this DbSet name; we'll also pin the table name below.
        public DbSet<CobaltUserKeyVault> CobaltUserKeyVaults { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Friend links
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

            // KeyVault mapping + 1–1 link
            modelBuilder.Entity<CobaltUserKeyVault>(b =>
            {
                // ⬇️ match your actual SQL table name
                b.ToTable("CobaltUserKeyVaults");

                // PK is UserId (1–1)
                b.HasKey(v => v.UserId);

                // required fields
                b.Property(v => v.EncPrivKeyPem).IsRequired();
                b.Property(v => v.PubKeyPem).IsRequired();

                // optional long text
                b.Property(v => v.EncRatchetPrivKeyJson); // nvarchar(max) via model attribute is fine
            });

               modelBuilder.Entity<User>()
                .HasOne(u => u.KeyVault)
                .WithOne(v => v.User)
                .HasForeignKey<CobaltUserKeyVault>(v => v.UserId)
                .OnDelete(DeleteBehavior.Cascade); // or .Restrict() if you don't want auto-delete
        }
    }
}
