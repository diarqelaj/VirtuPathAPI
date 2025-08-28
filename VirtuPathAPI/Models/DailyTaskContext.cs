using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class DailyTaskContext : DbContext
    {
        public DailyTaskContext(DbContextOptions<DailyTaskContext> options) : base(options) { }

        public DbSet<DailyTask>           DailyTasks          { get; set; } = default!;
        public DbSet<User>                Users               { get; set; } = default!;
        public DbSet<TaskCompletion>      TaskCompletions     { get; set; } = default!;
        public DbSet<UserCareerProgress>  UserCareerProgresses{ get; set; } = default!;

        // If this context is also used by TaskCompletionController analytics, expose them too:
        public DbSet<UserSubscription>    UserSubscriptions   { get; set; } = default!;
        public DbSet<PerformanceReview>   PerformanceReviews  { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.HasDefaultSchema("dbo");

           modelBuilder.Entity<UserCareerProgress>(e =>
            {
                e.ToTable("UserCareerProgress", "dbo");

                // ✅ Tell EF what the primary key is
                e.HasKey(x => new { x.UserID, x.CareerPathID });

                // (Optional) you can keep the unique index, but it’s redundant with the PK
                // e.HasIndex(x => new { x.UserID, x.CareerPathID }).IsUnique();
            });

            // optional: map other tables to dbo explicitly
            modelBuilder.Entity<DailyTask>().ToTable("DailyTasks", "dbo");
            modelBuilder.Entity<TaskCompletion>().ToTable("TaskCompletion", "dbo");
            modelBuilder.Entity<User>().ToTable("Users", "dbo");
            modelBuilder.Entity<UserSubscription>().ToTable("UserSubscriptions", "dbo");
            modelBuilder.Entity<PerformanceReview>().ToTable("PerformanceReviews", "dbo");
        }
    }
}
