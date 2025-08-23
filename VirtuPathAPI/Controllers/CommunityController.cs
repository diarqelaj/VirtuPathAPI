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
                    imageUrl = p.ImageUrl,                           // can be Cloudinary or legacy "/post-images/.."
                    imagePublicId = TryExtractPublicId(p.ImageUrl),  // FE can use this for transformed delivery
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

        // Preferred JSON body: { content, imageUrl? }
        public class CreatePostJsonRequest
        {
            public string? Content { get; set; }
            public string? ImageUrl { get; set; }        // e.g. https://res.cloudinary.com/...
            public string? ImagePublicId { get; set; }   // optional, not persisted
        }

        // Legacy multipart fallback
        public class CreatePostMultipartRequest
        {
            [FromForm(Name = "content")] public string? Content { get; set; }
            [FromForm(Name = "image")] public IFormFile? Image { get; set; }
        }

        /// <summary>
        /// Preferred: application/json → { content, imageUrl }
        /// - If imageUrl is a Cloudinary URL, we normalize it to a .webp delivery URL.
        /// </summary>
        [HttpPost("posts")]
        [Consumes("application/json")]
        public async Task<IActionResult> CreatePostJson([FromBody] CreatePostJsonRequest body)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });

            if (string.IsNullOrWhiteSpace(body?.Content) && string.IsNullOrWhiteSpace(body?.ImageUrl))
                return BadRequest(new { error = "Either content or imageUrl is required." });

            var post = new CommunityPost
            {
                UserID = userId.Value,
                Content = body?.Content?.Trim() ?? "",
                ImageUrl = NormalizeToWebp(string.IsNullOrWhiteSpace(body?.ImageUrl) ? null : body!.ImageUrl!.Trim()),
                CreatedAt = DateTime.UtcNow
            };

            _postContext.CommunityPosts.Add(post);
            await _postContext.SaveChangesAsync();

            return Ok(new
            {
                id = post.PostId,
                content = post.Content,
                imageUrl = post.ImageUrl,
                imagePublicId = TryExtractPublicId(post.ImageUrl),
                createdAt = post.CreatedAt
            });
        }

        /// <summary>
        /// Legacy: multipart/form-data (upload file directly to server -> Cloudinary).
        /// We store as WebP in Cloudinary.
        /// </summary>
        [HttpPost("posts/file")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> CreatePostMultipart([FromForm] CreatePostMultipartRequest form)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized(new { error = "Must be logged in." });

            if (string.IsNullOrWhiteSpace(form?.Content) && (form?.Image == null || form.Image.Length == 0))
                return BadRequest(new { error = "Either content or image file is required." });

            var post = new CommunityPost
            {
                UserID = userId.Value,
                Content = form?.Content?.Trim() ?? "",
                CreatedAt = DateTime.UtcNow
            };

            if (form?.Image != null && form.Image.Length > 0)
            {
                if (_cloud == null)
                    return StatusCode(500, new { error = "Cloudinary is not configured on the server." });

                var allowedExt = new[] { ".jpg", ".jpeg", ".png", ".webp" };
                var ext = Path.GetExtension(form.Image.FileName).ToLowerInvariant();
                if (!allowedExt.Contains(ext))
                    return BadRequest(new { error = "Invalid file type." });

                var uploadParams = new ImageUploadParams
                {
                    File = new FileDescription(form.Image.FileName, form.Image.OpenReadStream()),
                    Folder = "virtupath/community/posts",
                    Format = "webp", // force webp storage
                    Transformation = new Transformation()
                        .Width(1280).Height(720).Crop("limit").Quality("auto")
                };

                var result = await _cloud.UploadAsync(uploadParams);
                if (result.StatusCode != System.Net.HttpStatusCode.OK)
                    return StatusCode(500, new { error = "Cloudinary upload failed." });

                post.ImageUrl = result.SecureUrl.ToString(); // ends with .webp
            }

            _postContext.CommunityPosts.Add(post);
            await _postContext.SaveChangesAsync();

            return Ok(new
            {
                id = post.PostId,
                content = post.Content,
                imageUrl = post.ImageUrl,
                imagePublicId = TryExtractPublicId(post.ImageUrl),
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

            // Try to remove legacy local file if needed
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
        private static bool IsCloudinaryUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.IndexOf("res.cloudinary.com/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Extract Cloudinary public_id from a secure URL.
        /// Example:
        ///   https://res.cloudinary.com/<cloud>/image/upload/v1724200000/folder/name/abc123.webp
        ///   -> folder/name/abc123
        /// </summary>
        private static string? TryExtractPublicId(string? secureUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(secureUrl)) return null;

                var uri = new Uri(secureUrl);
                var path = uri.AbsolutePath; // /<cloudinary_path>/image/upload/.../<publicid>.<ext>
                if (path.StartsWith("/")) path = path.Substring(1);

                // Works for both image and video resources
                var markerIdx = path.IndexOf("/image/upload/", StringComparison.OrdinalIgnoreCase);
                if (markerIdx < 0) markerIdx = path.IndexOf("/video/upload/", StringComparison.OrdinalIgnoreCase);
                if (markerIdx < 0) return null;

                var after = path.Substring(markerIdx + "/image/upload/".Length); // ok even if it was /video/upload/
                after = Regex.Replace(after, @"^v\d+\/", "");        // drop version folder
                after = Regex.Replace(after, @"\.[a-zA-Z0-9]+$", ""); // drop extension
                return after;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// If it's a Cloudinary URL:
        ///  - leave as-is if already .webp
        ///  - otherwise rewrite to a clean .webp delivery URL for the same public_id
        /// If not Cloudinary, return as-is.
        /// </summary>
        private string? NormalizeToWebp(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            if (!IsCloudinaryUrl(url)) return url;
            if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return url;

            var publicId = TryExtractPublicId(url);
            if (string.IsNullOrWhiteSpace(publicId)) return url;

            var cloudName = _cloud?.Api?.Account?.Cloud;
            if (string.IsNullOrWhiteSpace(cloudName)) return url;

            // Clean, no-transform, .webp delivery URL.
            return $"https://res.cloudinary.com/{cloudName}/image/upload/{publicId}.webp";
        }
    }
}
