using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VirtuPathAPI.Models;
using SixLabors.ImageSharp.Formats.Webp;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CommunityController : ControllerBase
    {
        private readonly CommunityPostContext _postContext;
        private readonly UserContext _userContext;
        private readonly IWebHostEnvironment _env;

        public CommunityController(
            CommunityPostContext postContext,
            UserContext userContext,
            IWebHostEnvironment env)
        {
            _postContext = postContext;
            _userContext = userContext;
            _env = env;
        }

        // GET api/community/members
        [HttpGet("members")]
        public async Task<IActionResult> GetMembers()
        {
            var members = await _userContext.Users
                .Select(u => new {
                    id = u.UserID,
                    username = u.Username,
                    avatar = u.ProfilePictureUrl,
                    bio = u.Bio
                })
                .ToListAsync();
            return Ok(members);
        }

        // GET api/community/posts
        [HttpGet("posts")]
        public async Task<IActionResult> GetPosts()
        {
            var posts = await _postContext.CommunityPosts
                .Include(p => p.Author)
                .Include(p => p.Comments).ThenInclude(c => c.User)
                .Include(p => p.Comments).ThenInclude(c => c.Replies).ThenInclude(r => r.User)
                .Include(p => p.Reactions)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new {
                    id = p.PostId,
                    content = p.Content,
                    imageUrl = p.ImageUrl,
                    createdAt = p.CreatedAt,
                    author = new
                    {
                        id = p.Author.UserID,
                        username = p.Author.Username,
                        avatar = p.Author.ProfilePictureUrl
                    },
                    comments = p.Comments
    .Where(c => c.ParentCommentId == null)
    .OrderBy(c => c.CreatedAt)
    .Select(c => new
    {
        id = c.CommentId,
        content = c.Content,
        createdAt = c.CreatedAt,
        author = new
        {
            id = c.User.UserID,
            username = c.User.Username,
            avatar = c.User.ProfilePictureUrl
        },
        likeCount = _postContext.CommentReactions.Count(r => r.CommentId == c.CommentId),
        replies = c.Replies
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                id = r.CommentId,
                content = r.Content,
                createdAt = r.CreatedAt,
                author = new
                {
                    id = r.User.UserID,
                    username = r.User.Username,
                    avatar = r.User.ProfilePictureUrl
                }
            })
    }),

                    likeCount = p.Reactions.Count(r => r.Type == ReactionType.Like),
                    dislikeCount = p.Reactions.Count(r => r.Type == ReactionType.Dislike)
                })
                .ToListAsync();

            return Ok(posts);
        }

        [HttpPost("comments/{commentId}/like")]
public async Task<IActionResult> LikeComment(int commentId)
{
    var userId = HttpContext.Session.GetInt32("UserID");
    if (userId == null) return Unauthorized();

    var existing = await _postContext.CommentReactions
        .FirstOrDefaultAsync(r => r.CommentId == commentId && r.UserID == userId);

    if (existing != null)
    {
        // unlike
        _postContext.CommentReactions.Remove(existing);
    }
    else
    {
        _postContext.CommentReactions.Add(new CommentReaction
        {
            CommentId = commentId,
            UserID = userId.Value,
            CreatedAt = DateTime.UtcNow
        });
    }

    await _postContext.SaveChangesAsync();

    var likeCount = await _postContext.CommentReactions.CountAsync(r => r.CommentId == commentId);
    return Ok(new { likeCount });
}

        // POST api/community/posts
        [HttpPost("posts")]
        public async Task<IActionResult> CreatePost([FromForm] CreatePostWithImageRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null)
                return Unauthorized(new { error = "Must be logged in." });

            if (string.IsNullOrWhiteSpace(req.Content) && req.Image == null)
                return BadRequest(new { error = "Text or image required." });

            var post = new CommunityPost
            {
                UserID = userId.Value,
                Content = req.Content?.Trim() ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };

            if (req.Image != null && req.Image.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
                var ext = Path.GetExtension(req.Image.FileName).ToLower();
                if (!allowedExtensions.Contains(ext))
                    return BadRequest(new { error = "Only .jpg, .jpeg, .png, and .webp allowed." });

                if (req.Image.Length > 5 * 1024 * 1024)
                    return BadRequest(new { error = "Image must be under 5MB." });

                var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "post-images");
                if (!Directory.Exists(uploadsFolder))
                    Directory.CreateDirectory(uploadsFolder);

                var webpFileName = $"{Guid.NewGuid()}.webp";
                var webpPath = Path.Combine(uploadsFolder, webpFileName);

                using (var image = await Image.LoadAsync(req.Image.OpenReadStream()))
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(1080, 1080)
                    }));

                    await image.SaveAsync(webpPath, new WebpEncoder { Quality = 85 });
                }

                post.ImageUrl = $"/post-images/{webpFileName}";
            }

            _postContext.CommunityPosts.Add(post);
            await _postContext.SaveChangesAsync();

            return Ok(new
            {
                id = post.PostId,
                content = post.Content,
                imageUrl = post.ImageUrl,
                createdAt = post.CreatedAt
            });
        }



        // POST api/community/posts/{postId}/comments
        [HttpPost("posts/{postId}/comments")]
        public async Task<IActionResult> AddComment(int postId, [FromBody] AddCommentRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });
            if (string.IsNullOrWhiteSpace(req.Content))
                return BadRequest(new { error = "Content required." });

            var comment = new Comment
            {
                PostId = postId,
                UserID = userId.Value,
                Content = req.Content.Trim(),
                ParentCommentId = req.ParentCommentId,
                CreatedAt = DateTime.UtcNow
            };
            _postContext.Comments.Add(comment);
            await _postContext.SaveChangesAsync();

            return Ok(new
            {
                id = comment.CommentId,
                content = comment.Content,
                createdAt = comment.CreatedAt,
                parentComment = comment.ParentCommentId
            });
        }
        [HttpDelete("posts/{postId}")]
        public async Task<IActionResult> DeletePost(int postId)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null)
                return Unauthorized(new { error = "Must be logged in." });

            var post = await _postContext.CommunityPosts.FindAsync(postId);
            if (post == null)
                return NotFound(new { error = "Post not found." });

            if (post.UserID != userId)
                return Forbid("You can only delete your own posts.");

            // Delete post image from disk if it exists
            if (!string.IsNullOrEmpty(post.ImageUrl))
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", post.ImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }

            _postContext.CommunityPosts.Remove(post);
            await _postContext.SaveChangesAsync();

            return Ok(new { message = "Post deleted." });
        }


        // POST api/community/posts/{postId}/reactions
        [HttpPost("posts/{postId}/reactions")]
        public async Task<IActionResult> React(int postId, [FromBody] ReactionRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });

            var existing = await _postContext.Reactions
                .FirstOrDefaultAsync(r => r.PostId == postId && r.UserID == userId.Value);

            if (existing != null && existing.Type == req.Type)
                _postContext.Reactions.Remove(existing);
            else if (existing != null)
            {
                existing.Type = req.Type;
                existing.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                _postContext.Reactions.Add(new Reaction
                {
                    PostId = postId,
                    UserID = userId.Value,
                    Type = req.Type,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _postContext.SaveChangesAsync();

            var likeCount = await _postContext.Reactions.CountAsync(r => r.PostId == postId && r.Type == ReactionType.Like);
            var dislikeCount = await _postContext.Reactions.CountAsync(r => r.PostId == postId && r.Type == ReactionType.Dislike);

            return Ok(new { likeCount, dislikeCount });
        }

        // DTOs
        public class CreatePostWithImageRequest
        {
            [FromForm(Name = "content")] public string? Content { get; set; }
            [FromForm(Name = "image")] public IFormFile? Image { get; set; }
        }

        public class AddCommentRequest
        {
            public string? Content { get; set; }
            public int? ParentCommentId { get; set; }
        }

        public class ReactionRequest
        {
            public ReactionType Type { get; set; }
        }
    }
}
