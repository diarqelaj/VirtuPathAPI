using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Data
{
    public class ReviewContext : DbContext
    {
        public ReviewContext(DbContextOptions<ReviewContext> options) : base(options) { }

        public DbSet<Review> Reviews { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Helpful index for listing/averages per career path
            modelBuilder.Entity<Review>()
                .HasIndex(r => new { r.CareerPathID });

            // If your DB table/columns already exist, EF just maps to them.
            base.OnModelCreating(modelBuilder);
        }
    }
}
