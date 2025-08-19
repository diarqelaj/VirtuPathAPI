using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class TaskCompletionContext : DbContext
    {
        public TaskCompletionContext(DbContextOptions<TaskCompletionContext> options) : base(options) {}

        public DbSet<TaskCompletion> TaskCompletions { get; set; } = null!;
        public DbSet<UserSubscription> UserSubscriptions { get; set; } = null!;
        public DbSet<DailyTask> DailyTasks { get; set; } = null!;
        public DbSet<PerformanceReview> PerformanceReviews { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TaskCompletion>(e =>
            {
                e.ToTable("TaskCompletion");
                e.HasKey(tc => tc.CompletionID);
            });

            modelBuilder.Entity<UserSubscription>(e =>
            {
                e.ToTable("UserSubscriptions");
                e.HasKey(us => us.Id);                // was SubscriptionID
                e.Property(us => us.Plan)
                 .HasColumnName("Plan");              // reserved word mapping
            });

            modelBuilder.Entity<DailyTask>(e =>
            {
                e.ToTable("DailyTasks");
                e.HasKey(dt => dt.TaskID);
            });

            modelBuilder.Entity<PerformanceReview>(e =>
            {
                e.ToTable("PerformanceReviews");
                e.HasKey(pr => pr.ReviewID);
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
