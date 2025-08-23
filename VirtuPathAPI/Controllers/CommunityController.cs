using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CommunityController : ControllerBase
    {
        private readonly CommunityPostContext _postContext;
        private readonly UserContext _userContext;
        private readonly Cloudinary? _cloud; // optional

        public CommunityController(
            CommunityPostContext postContext,
            UserContext userContext,
            Cloudinary? cloud // registered in Program.cs when CLOUDINARY_URL exists
        )
        {
            _postContext = postContext;
            _userContext = userContext;
            _cloud = cloud;
        }

        // ---------------- Members ----------------
        [HttpGet("members")]
        public async Task<IActionResult> GetMembers()
        {
            var members = await _userContext.Users
                .Select(u => new
                {
                    id = u.UserID,
                    username = u.Username,
                    avatar = u.ProfilePictureUrl,
                    bio = u.Bio
                })
                .ToListAsync();

            return Ok(members);
        }

        // ---------------- Posts (list) ----------------
        [HttpGet("posts")]
        public async Task<IActionResult> GetPosts()
        {
            var posts = await _postContext.CommunityPosts
                .Include(p => p.Author)
                .Include(p => p.Comments).ThenInclude(c => c.User)
                .Include(p => p.Comments).ThenInclude(c => c.Replies).ThenInclude(r => r.User)
                .Include(p => p.Reactions)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new
                {
                    id = p.PostId,
                    content = p.Content,
                    imageUrl = p.ImageUrl,          // full URL (Cloudinary or legacy /post-images/..)
                    imagePublicId = (string?)null,  // kept for FE shape; we didn't persist it
                    createdAt = p.CreatedAt,
                    author = new
                    {
                        id = p.Author!.UserID,
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
                                id = c.User!.UserID,
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
                                        id = r.User!.UserID,
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

        // ---------------- Comments: like/unlike ----------------
        [HttpPost("comments/{commentId}/like")]
        public async Task<IActionResult> LikeComment(int commentId)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized();

            var existing = await _postContext.CommentReactions
                .FirstOrDefaultAsync(r => r.CommentId == commentId && r.UserID == userId);

            if (existing != null)
            {
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

            var likeCount = await _postContext.CommentReactions
                .CountAsync(r => r.CommentId == commentId);

            return Ok(new { likeCount });
        }

        // ---------------- Posts: create (JSON or multipart fallback) ----------------

        // New JSON body (preferred): { content, imageUrl?, imagePublicId? }
        public class CreatePostJsonRequest
        {
            public string? Content { get; set; }
            public string? ImageUrl { get; set; }        // e.g. https://res.cloudinary.com/...
            public string? ImagePublicId { get; set; }   // optional, not persisted
        }

        // Legacy multipart fallback (kept for compatibility)
        public class CreatePostMultipartRequest
        {
            [FromForm(Name = "content")] public string? Content { get; set; }
            [FromForm(Name = "image")] public IFormFile? Image { get; set; }
        }

        /// <summary>
        /// Preferred: application/json → { content, imageUrl, imagePublicId }
        /// </summary>
        [HttpPost("posts")]
        [Consumes("application/json")]
        public async Task<IActionResult> CreatePostJson([FromBody] CreatePostJsonRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });

            if (string.IsNullOrWhiteSpace(req.Content) && string.IsNullOrWhiteSpace(req.ImageUrl))
                return BadRequest(new { error = "Text or image required." });

            var post = new CommunityPost
            {
                UserID = userId.Value,
                Content = (req.Content ?? string.Empty).Trim(),
                CreatedAt = DateTime.UtcNow,
                ImageUrl = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl!.Trim()
            };

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

        /// <summary>
        /// Legacy: multipart/form-data (kept in case old clients still post files).
        /// </summary>
        [HttpPost("posts")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> CreatePostMultipart([FromForm] CreatePostMultipartRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });

            if (string.IsNullOrWhiteSpace(req.Content) && req.Image == null)
                return BadRequest(new { error = "Text or image required." });

            var post = new CommunityPost
            {
                UserID = userId.Value,
                Content = (req.Content ?? string.Empty).Trim(),
                CreatedAt = DateTime.UtcNow
            };

            // If someone still uploads a file to the API directly and Cloudinary is configured,
            // we’ll stream it to Cloudinary; if not configured, we’ll reject it (no disk writes).
            if (req.Image != null && req.Image.Length > 0)
            {
                if (_cloud == null)
                    return BadRequest(new { error = "Image uploads must go to Cloudinary from the client." });

                var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp" };
                var ext = Path.GetExtension(req.Image.FileName).ToLowerInvariant();
                if (!allowed.Contains(ext))
                    return BadRequest(new { error = "Only .jpg, .jpeg, .png, .webp allowed." });
                if (req.Image.Length > 5 * 1024 * 1024)
                    return BadRequest(new { error = "Image must be under 5MB." });

                await using var stream = req.Image.OpenReadStream();
                var up = new ImageUploadParams
                {
                    File = new FileDescription(req.Image.FileName, stream),
                    Folder = "virtupath/community/posts",
                    Format = "webp",
                    Transformation = new Transformation().Quality("auto")
                };
                var result = await _cloud.UploadAsync(up);
                if (result.StatusCode != System.Net.HttpStatusCode.OK)
                    return StatusCode(500, new { error = "Cloudinary upload failed." });

                post.ImageUrl = result.SecureUrl?.ToString();
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

        // ---------------- Comments: add ----------------
        public class AddCommentRequest
        {
            public string? Content { get; set; }
            public int? ParentCommentId { get; set; }
        }

        [HttpPost("posts/{postId}/comments")]
        public async Task<IActionResult> AddComment(int postId, [FromBody] AddCommentRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });
            if (string.IsNullOrWhiteSpace(req.Content))
                return BadRequest(new { error = "Content required." });

            // Ensure post exists
            var postExists = await _postContext.CommunityPosts.AnyAsync(p => p.PostId == postId);
            if (!postExists) return NotFound(new { error = "Post not found." });

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

        // ---------------- Reactions ----------------
        public class ReactionRequest
        {
            public ReactionType Type { get; set; }
        }

        [HttpPost("posts/{postId}/reactions")]
        public async Task<IActionResult> React(int postId, [FromBody] ReactionRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });

            var existing = await _postContext.Reactions
                .FirstOrDefaultAsync(r => r.PostId == postId && r.UserID == userId.Value);

            if (existing != null && existing.Type == req.Type)
            {
                _postContext.Reactions.Remove(existing);
            }
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

            var likeCount = await _postContext.Reactions
                .CountAsync(r => r.PostId == postId && r.Type == ReactionType.Like);
            var dislikeCount = await _postContext.Reactions
                .CountAsync(r => r.PostId == postId && r.Type == ReactionType.Dislike);

            return Ok(new { likeCount, dislikeCount });
        }

        // ---------------- Delete Post (author or admin) ----------------
        [HttpDelete("posts/{postId}")]
        public async Task<IActionResult> DeletePost(int postId)
        {
            var sid = HttpContext.Session.GetInt32("UserID");
            if (sid == null) return Unauthorized(new { error = "Must be logged in." });

            var me = await _userContext.Users
                .Where(u => u.UserID == sid.Value)
                .Select(u => new { u.UserID, u.IsOfficial /* TODO: swap to u.IsAdmin if you have it */ })
                .FirstOrDefaultAsync();

            if (me == null) return Unauthorized(new { error = "User not found." });

            var post = await _postContext.CommunityPosts.FindAsync(postId);
            if (post == null) return NotFound(new { error = "Post not found." });

            var isAdmin = me.IsOfficial;           // TODO: swap to IsAdmin if available
            var isAuthor = (post.UserID == me.UserID);
            if (!isAuthor && !isAdmin)
                return Forbid("Only the author or an admin can delete this post.");

            // Try Cloudinary deletion if possible
            if (_cloud != null && !string.IsNullOrWhiteSpace(post.ImageUrl) && IsCloudinaryUrl(post.ImageUrl))
            {
                var publicId = TryExtractPublicId(post.ImageUrl!);
                if (!string.IsNullOrWhiteSpace(publicId))
                {
                    try { await _cloud.DestroyAsync(new DeletionParams(publicId)); }
                    catch { /* don't fail deletion if cloud destroy fails */ }
                }
            }

            // If you still have very old posts saved on disk (legacy), try to delete them
            if (!string.IsNullOrWhiteSpace(post.ImageUrl) && post.ImageUrl!.StartsWith("/"))
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", post.ImageUrl!.TrimStart('/'));
                if (System.IO.File.Exists(filePath))
                {
                    try { System.IO.File.Delete(filePath); } catch { /* ignore */ }
                }
            }

            _postContext.CommunityPosts.Remove(post);
            await _postContext.SaveChangesAsync();

            return Ok(new { message = "Post deleted." });
        }

        // ---------------- Helpers ----------------
        private static bool IsCloudinaryUrl(string url)
        {
            return url.Contains("res.cloudinary.com/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extract Cloudinary public_id from a secure URL.
        /// Example:
        ///   https://res.cloudinary.com/<cloud>/image/upload/v1724200000/folder/name/abc123.webp
        ///   -> folder/name/abc123
        /// </summary>
        private static string? TryExtractPublicId(string secureUrl)
        {
            try
            {
                var uri = new Uri(secureUrl);
                var path = uri.AbsolutePath; // /<cloudinary_path>/image/upload/.../<publicid>.<ext>
                // Strip leading "/"
                if (path.StartsWith("/")) path = path.Substring(1);

                // Find the first "image/upload" (or "video/upload") segment
                var idx = path.IndexOf("/image/upload/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = path.IndexOf("/video/upload/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;

                var after = path.Substring(idx + "/image/upload/".Length); // ok even if it was /video/upload/
                // Remove version prefix like v1234567890/
                after = Regex.Replace(after, @"^v\d+\/", "");

                // Remove extension at the end
                after = Regex.Replace(after, @"\.[a-zA-Z0-9]+$", "");

                return after;
            }
            catch
            {
                return null;
            }
        }
    }
}
