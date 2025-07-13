using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Models
{
    public class CommunityPostContext : DbContext
    {
        public CommunityPostContext(DbContextOptions<CommunityPostContext> options)
            : base(options) { }

        public DbSet<CommunityPost> CommunityPosts { get; set; }
        public DbSet<Comment> Comments { get; set; }
        public DbSet<Reaction> Reactions { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<CommentReaction> CommentReactions { get; set; }


        protected override void OnModelCreating(ModelBuilder mb)
        {
            // Map Users → dbo.Users
            mb.Entity<User>(eb =>
            {
                eb.ToTable("Users");
                eb.HasKey(u => u.UserID);
            });

            // CommunityPosts
            mb.Entity<CommunityPost>(eb =>
            {
                eb.ToTable("CommunityPosts");
                eb.HasKey(p => p.PostId);
                eb.Property(p => p.Content).IsRequired();
                eb.Property(p => p.ImageUrl)
                  .HasMaxLength(1000)
                  .IsRequired(false);
                eb.Property(p => p.CreatedAt)
                  .HasDefaultValueSql("SYSUTCDATETIME()");
                eb.HasOne(p => p.Author)
                  .WithMany()
                  .HasForeignKey(p => p.UserID)
                  .OnDelete(DeleteBehavior.Cascade);
            });

            // Comments
            mb.Entity<Comment>(eb =>
            {
                eb.ToTable("Comments");
                eb.HasKey(c => c.CommentId);
                eb.Property(c => c.Content).IsRequired();
                eb.Property(c => c.CreatedAt)
                  .HasDefaultValueSql("SYSUTCDATETIME()");
                eb.HasOne(c => c.Post)
                  .WithMany(p => p.Comments)
                  .HasForeignKey(c => c.PostId)
                  .OnDelete(DeleteBehavior.Cascade);
                eb.HasOne(c => c.User)
                  .WithMany()
                  .HasForeignKey(c => c.UserID)
                  .OnDelete(DeleteBehavior.NoAction);
                eb.HasOne(c => c.ParentComment)
                  .WithMany(c => c.Replies)
                  .HasForeignKey(c => c.ParentCommentId)
                  .OnDelete(DeleteBehavior.NoAction);
            });
            mb.Entity<CommentReaction>(eb =>
            {
                eb.ToTable("CommentReactions");
                eb.HasKey(cr => cr.CommentReactionId);
                eb.Property(cr => cr.CreatedAt)
                  .HasDefaultValueSql("SYSUTCDATETIME()");
                eb.HasOne(cr => cr.Comment)
                  .WithMany()
                  .HasForeignKey(cr => cr.CommentId)
                  .OnDelete(DeleteBehavior.Cascade);
                eb.HasOne(cr => cr.User)
                  .WithMany()
                  .HasForeignKey(cr => cr.UserID)
                  .OnDelete(DeleteBehavior.NoAction);
            });

            // Reactions
            mb.Entity<Reaction>(eb =>
            {
                eb.ToTable("Reactions");
                eb.HasKey(r => r.ReactionId);
                eb.Property(r => r.CreatedAt)
                  .HasDefaultValueSql("SYSUTCDATETIME()");
                eb.HasOne(r => r.Post)
                  .WithMany(p => p.Reactions)
                  .HasForeignKey(r => r.PostId)
                  .OnDelete(DeleteBehavior.Cascade);
                eb.HasOne(r => r.User)
                  .WithMany()
                  .HasForeignKey(r => r.UserID)
                  .OnDelete(DeleteBehavior.NoAction);
            });
        }
    }
}
