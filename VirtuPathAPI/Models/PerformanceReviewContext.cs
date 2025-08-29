using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class PerformanceReviewContext : DbContext
    {
        public PerformanceReviewContext(DbContextOptions<PerformanceReviewContext> options)
            : base(options) { }

        public DbSet<PerformanceReview> PerformanceReviews { get; set; }

        // These let the controller read everything it needs
        public DbSet<DailyTask> DailyTasks { get; set; }
        public DbSet<TaskCompletion> TaskCompletions { get; set; }
        public DbSet<User> Users { get; set; }

        // OPTIONAL: lock table names if your tables aren’t the default plural forms
        // protected override void OnModelCreating(ModelBuilder b)
        // {
        //     b.Entity<User>().ToTable("Users");
        //     b.Entity<DailyTask>().ToTable("DailyTasks");
        //     b.Entity<TaskCompletion>().ToTable("TaskCompletions");
        //     b.Entity<PerformanceReview>().ToTable("PerformanceReviews");
        //     base.OnModelCreating(b);
        // }
    }
}
