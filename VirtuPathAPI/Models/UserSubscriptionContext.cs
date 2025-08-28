using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Models
{
    public class UserSubscriptionContext : DbContext
    {
        public UserSubscriptionContext(DbContextOptions<UserSubscriptionContext> options) : base(options) { }

        public DbSet<UserSubscription> UserSubscriptions { get; set; } = default!;
        public DbSet<PriceMap>         PriceMaps        { get; set; } = default!;
        public DbSet<User>             Users            { get; set; } = default!; // see note (2)

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Default schema
            modelBuilder.HasDefaultSchema("dbo");

            // -------- PriceMap --------
            modelBuilder.Entity<PriceMap>(e =>
            {
                e.ToTable("PriceMaps", "dbo");
                e.HasKey(x => x.PaddlePriceId);
                e.Property(x => x.PaddlePriceId).HasMaxLength(256).IsRequired();
                e.Property(x => x.PlanName).HasMaxLength(64).IsRequired();
                e.Property(x => x.Billing).HasMaxLength(64).IsRequired();
                e.Property(x => x.Active).HasDefaultValue(true);
            });

            // -------- UserSubscription --------
            modelBuilder.Entity<UserSubscription>(e =>
            {
                e.ToTable("UserSubscriptions", "dbo");

                // (Optional but recommended) unique per user/path
                // e.HasIndex(x => new { x.UserID, x.CareerPathID }).IsUnique();

                // Map computed end-date correctly and prevent EF from writing it
                e.Property(x => x.CurrentPeriodEnd)
                 .HasColumnName("EndDate")
                 .HasColumnType("date")
                 .ValueGeneratedOnAddOrUpdate();

                // EF should not try to send this column on UPDATE
                e.Property(x => x.CurrentPeriodEnd).Metadata
                 .SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

                // (These are already mapped via data annotations on the model,
                //  but it's fine to be explicit—uncomment if you want explicit mapping)
                //
                // e.Property(x => x.Id).HasColumnName("SubscriptionID").ValueGeneratedOnAdd();
                // e.Property(x => x.UserID).HasColumnName("UserID");
                // e.Property(x => x.CareerPathID).HasColumnName("CareerPathID");
                // e.Property(x => x.Plan).HasColumnName("PlanName").HasMaxLength(32).IsRequired();
                // e.Property(x => x.Billing).HasColumnName("Billing").HasMaxLength(32).IsRequired();
                // e.Property(x => x.PaddleSubscriptionId).HasColumnName("PaddlePriceId").HasMaxLength(128);
                // e.Property(x => x.LastTransactionId).HasColumnName("PaddleTransactionId").HasMaxLength(128);
                // e.Property(x => x.StartAt).HasColumnName("StartDate").HasColumnType("date");
                // e.Property(x => x.LastAccessedDay).HasColumnName("LastAccessedDay");
            });

            // -------- User --------
            modelBuilder.Entity<User>(e =>
            {
                e.ToTable("Users", "dbo");
            });
        }
    }
}
