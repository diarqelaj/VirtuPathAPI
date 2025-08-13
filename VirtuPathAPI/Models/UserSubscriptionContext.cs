using Microsoft.EntityFrameworkCore;
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

        modelBuilder.HasDefaultSchema("dbo");

        modelBuilder.Entity<PriceMap>(e =>
        {
            e.ToTable("PriceMaps", "dbo");
            e.HasKey(x => x.PaddlePriceId);
            e.Property(x => x.PaddlePriceId).HasMaxLength(256).IsRequired();
            e.Property(x => x.PlanName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Billing).HasMaxLength(64).IsRequired();
            e.Property(x => x.Active).HasDefaultValue(true);
        });

        // (Optional) be explicit for your other entities too
        modelBuilder.Entity<UserSubscription>().ToTable("UserSubscriptions", "dbo");
        modelBuilder.Entity<User>().ToTable("Users", "dbo");
    }
}

}
