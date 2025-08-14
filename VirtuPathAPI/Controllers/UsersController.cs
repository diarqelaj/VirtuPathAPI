using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using BCrypt.Net;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;
using System.Text.Json.Serialization;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using ImgSize = SixLabors.ImageSharp.Size;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
   public class UsersController : ControllerBase
{
    private readonly UserContext _context;
    private static readonly HashSet<string> BannedIPs = new();
    private readonly IDataProtector _protector;
    private readonly Cloudinary _cloud;

    // ⬇️ inject IDataProtectionProvider and use it
    public UsersController(UserContext context, Cloudinary cloud, IDataProtectionProvider dataProtection)
    {
        _context = context;
        _cloud   = cloud;
        _protector = dataProtection.CreateProtector("VirtuPath.Keys.UserPrivate.v1");
    }

        // Ban an IP address
        [HttpPost("ban-ip")]
        public IActionResult BanIP([FromBody] BanIpRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Ip))
                return BadRequest(new { error = "IP address is required." });

            BannedIPs.Add(request.Ip.Trim());
            Console.WriteLine($"🚫 IP banned: {request.Ip}");
            return Ok(new { message = $"IP {request.Ip} has been banned." });
        }

        // Unban an IP address
        [HttpPost("unban-ip")]
        public IActionResult UnbanIP([FromBody] BanIpRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Ip))
                return BadRequest(new { error = "IP address is required." });

            bool removed = BannedIPs.Remove(request.Ip.Trim());
            if (removed)
            {
                Console.WriteLine($"✅ IP unbanned: {request.Ip}");
                return Ok(new { message = $"IP {request.Ip} has been unbanned." });
            }

            return NotFound(new { error = $"IP {request.Ip} was not found in the ban list." });
        }

        private bool IsIpBanned(string ip) => BannedIPs.Contains(ip);

        // ✅ GET all users (admin/debug only)
        [HttpGet]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            return await _context.Users.ToListAsync();
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            var ipToCheck = !string.IsNullOrEmpty(req.ClientPublicIp)
                            ? req.ClientPublicIp
                            : HttpContext.Connection.RemoteIpAddress?.ToString();
            if (ipToCheck == "::1") ipToCheck = "127.0.0.1";

           
            if (BannedIPs.Contains(ipToCheck))
                return Forbid($"Access denied from IP {ipToCheck}");

            if (string.IsNullOrWhiteSpace(req.Identifier))
                return BadRequest(new { error = "Email or username is required." });

            var identifier = req.Identifier.Trim().ToLower();
            var user = await _context.Users.FirstOrDefaultAsync(u =>
                u.Email.ToLower() == identifier || u.Username.ToLower() == identifier);

            if (user == null)
            {
             
                return Unauthorized(new { error = "User not found." });
            }

            // ✅ Password vs Google logic
            if (req.IsGoogleLogin)
            {
                // Allow login regardless of PasswordHash (account may have set a password later)
            
            }
            else
            {
                if (string.IsNullOrWhiteSpace(req.Password))
                {
                 
                    return Unauthorized(new { error = "Password required for this account." });
                }

                if (string.IsNullOrEmpty(user.PasswordHash) ||
                    !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                {
                
                    return Unauthorized(new { error = "Invalid password." });
                }
            }
            await EnsureUserCryptoAsync(user, allowServerPrivate: true);

            // Persist IP + last active
            user.LastKnownIP = ipToCheck;
            user.LastActiveAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (user.IsTwoFactorEnabled && !req.IsGoogleLogin)
                return Ok(new { requires2FA = true });

            HttpContext.Session.SetInt32("UserID", user.UserID);

            if (req.RememberMe)
            {
                Response.Cookies.Append("VirtuPathRemember", user.UserID.ToString(), new CookieOptions
                {
                    HttpOnly = false,
                    Secure = true,
                    SameSite = SameSiteMode.None,
                    Expires = DateTimeOffset.UtcNow.AddMonths(1),
                });
            }
          


            return Ok(new
            {
                userID = user.UserID,
                username = user.Username,
                fullName = user.FullName,
                profilePicture = user.ProfilePictureUrl
            });
        }

        // ✅ GET user by ID
        [HttpGet("{id}")]
        public async Task<ActionResult<User>> GetUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();
            return user;
        }

        private static readonly HashSet<string> ReservedUsernames = new HashSet<string>
        {
            "admin", "root", "system", "api", "login", "logout", "signup", "me", "settings",
            "dashboard", "virtu", "virtu-path-ai", "support", "terms", "privacy", "contact",
            "about", "pricing", "reset", "user", "users", "security", "public", "private",
            "team", "teams","virtupathai"," virtupathai.com","help", "faq"
        };

       [HttpPost]
public async Task<IActionResult> CreateUser([FromBody] User incoming)
{
    if (incoming is null)
        return BadRequest(new { error = "Missing request body." });

    // 1) Requireds
    var username = incoming.Username?.Trim().ToLower();
    var emailNorm = incoming.Email?.Trim().ToLower();

    if (string.IsNullOrWhiteSpace(emailNorm) ||
        string.IsNullOrWhiteSpace(incoming.PasswordHash) ||
        string.IsNullOrWhiteSpace(username))
    {
        return BadRequest(new { error = "Email, password, and username are required." });
    }

    // 2) Reserved / profanity / format
    if (ReservedUsernames.Contains(username))
        return BadRequest(new { error = "This username is reserved. Please choose another." });

    foreach (var word in ProfanityList.Words)
        if (username.Contains(word))
            return BadRequest(new { error = "Username contains inappropriate content." });

    if (!Regex.IsMatch(username, @"^[a-z0-9_]{3,20}$"))
        return BadRequest(new { error = "Username must be 3–20 chars, lowercase letters/numbers/underscores." });

    // 3) Uniqueness
    if (await _context.Users.AnyAsync(u => u.Username == username))
        return Conflict(new { error = "Username already taken" });

    if (await _context.Users.AnyAsync(u => u.Email.ToLower() == emailNorm))
        return Conflict(new { error = "Email already in use" });

    // 4) Build a fresh entity (never reuse the bound object)
    var user = new User
    {
        UserID             = 0, // guard against explicit ID in payload
        FullName           = incoming.FullName,
        Username           = username,
        Email              = emailNorm,
        PasswordHash       = BCrypt.Net.BCrypt.HashPassword(incoming.PasswordHash),
        RegistrationDate   = DateTime.UtcNow,

        // optional prefs from client (defaults are false in the model)
        ProductUpdates       = incoming.ProductUpdates,
        CareerTips           = incoming.CareerTips,
        NewCareerPathAlerts  = incoming.NewCareerPathAlerts,
        Promotions           = incoming.Promotions,
        IsProfilePrivate     = incoming.IsProfilePrivate,

        // safe defaults
        IsVerified         = false,
        IsOfficial         = false,
        IsTwoFactorEnabled = false,
        CurrentDay         = 0,

        // clear server-managed fields
        Bio                    = null,
        About                  = null,
        ProfilePictureUrl      = null,
        ProfilePicturePublicId = null,
        CoverImageUrl          = null,
        CoverImagePublicId     = null,
        PublicKeyJwk           = null,
        X25519PublicJwk        = null,
        KeyVault               = null, // IMPORTANT: avoid EF trying to insert principal twice
    };

    // 5) Save once to get identity
    _context.Users.Add(user);
    await _context.SaveChangesAsync();   // user.UserID is now set

    // 6) Best-effort crypto init; don't fail signup if it throws
    try
    {
        await EnsureUserCryptoAsync(user, allowServerPrivate: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CreateUser] key provision failed for user {user.UserID}: {ex.Message}");
        // Signup already succeeded; continue without blocking the response
    }

    return CreatedAtAction(nameof(GetUser), new { id = user.UserID }, user);
}


        [HttpGet("by-username/{username}")]
        public async Task<IActionResult> GetUserByUsername(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return BadRequest(new { error = "Username is required" });

            string normalized = username.Trim().ToLower();

            var user = await _context.Users
                .Where(u => u.Username.ToLower() == normalized)
                .Select(u => new
                {
                    u.UserID,
                    u.FullName,
                    u.Username,
                    u.Bio,
                    u.About,
                    u.ProfilePictureUrl,
                    u.CoverImageUrl,
                    u.IsProfilePrivate,
                    u.RegistrationDate,
                    u.IsVerified,
                    u.VerifiedDate,
                    u.IsOfficial,
                    u.LastActiveAt  // Include LastActiveAt in that projection if needed
                })
                .FirstOrDefaultAsync();

            if (user == null)
                return NotFound(new { error = "User not found" });

            return Ok(user);
        }

        [HttpGet("search")]
        public async Task<IActionResult> SearchUsersByName([FromQuery] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("Name is required.");

            var matches = await _context.Users
                .Where(u => u.FullName.ToLower().Contains(name.ToLower()))
                .Select(u => new
                {
                    u.UserID,
                    u.FullName,
                    u.Username,
                    u.ProfilePictureUrl,
                    u.IsVerified,
                    u.VerifiedDate,
                    u.IsOfficial,
                    u.LastActiveAt  // Include LastActiveAt in search results if desired
                })
                .ToListAsync();

            return Ok(matches);
        }

        // ✅ PUT /api/users/{id} — Update existing user
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUser(int id, User user)
        {
            if (id != user.UserID) return BadRequest();

            _context.Entry(user).State = EntityState.Modified;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPost("upload-profile-picture")]
        public async Task<IActionResult> UploadProfilePicture([FromForm] IFormFile file, [FromForm] int userId)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");
            if (file.Length > 5 * 1024 * 1024)
                return BadRequest("Image must be less than 5MB.");

            var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var ext     = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowed.Contains(ext))
                return BadRequest("Only .jpg, .jpeg, .png, .webp allowed.");

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            if (!string.IsNullOrEmpty(user.ProfilePicturePublicId))
                await _cloud.DestroyAsync(new DeletionParams(user.ProfilePicturePublicId));

            Stream uploadStream;
            string uploadFileName;

            if (ext != ".webp")
            {
                // load + convert
                await using var inStream = file.OpenReadStream();
                using     var img      = Image.Load(inStream);
                using     var ms       = new MemoryStream();
                await     img.SaveAsync(ms, new WebpEncoder());
                var bytes = ms.ToArray();

                uploadStream   = new MemoryStream(bytes);
                uploadFileName = Path.GetFileNameWithoutExtension(file.FileName) + ".webp";
            }
            else
            {
                uploadStream   = file.OpenReadStream();
                uploadFileName = file.FileName;
            }

            var uploadParams = new ImageUploadParams
            {
                File           = new FileDescription(uploadFileName, uploadStream),
                Folder         = "virtupath/users/profile",
                Format         = "webp",
                Transformation = new Transformation()
                    .Width(512).Height(512).Crop("limit")
                    .Quality("auto")
            };

            var result = await _cloud.UploadAsync(uploadParams);

            // clean up our stream
            uploadStream.Dispose();

            if (result.StatusCode != System.Net.HttpStatusCode.OK)
                return StatusCode(500, "Cloudinary upload failed.");

            user.ProfilePictureUrl      = result.SecureUrl.ToString();
            user.ProfilePicturePublicId = result.PublicId;
            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                profilePictureUrl      = user.ProfilePictureUrl,
                profilePicturePublicId = user.ProfilePicturePublicId
            });
        }


            [HttpDelete("delete-profile-picture")]
            public async Task<IActionResult> DeleteProfilePicture([FromQuery] int userId)
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return NotFound("User not found.");

                if (!string.IsNullOrEmpty(user.ProfilePicturePublicId))
                {
                    await _cloud.DestroyAsync(new DeletionParams(user.ProfilePicturePublicId));
                    user.ProfilePicturePublicId = null;
                }

                user.ProfilePictureUrl = null;
                _context.Users.Update(user);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Profile picture deleted." });
            }


      [HttpPost("upload-cover")]
        public async Task<IActionResult> UploadCoverImage([FromForm] IFormFile file, [FromForm] int userId)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");
            if (file.Length > 5 * 1024 * 1024)
                return BadRequest("Image must be less than 5MB.");

            var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var ext     = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowed.Contains(ext))
                return BadRequest("Only .jpg, .jpeg, .png, .webp allowed.");

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            if (!string.IsNullOrEmpty(user.CoverImagePublicId))
                await _cloud.DestroyAsync(new DeletionParams(user.CoverImagePublicId));

            Stream uploadStream;
            string uploadFileName;

            if (ext != ".webp")
            {
                await using var inStream = file.OpenReadStream();
                using     var img      = Image.Load(inStream);
                using     var ms       = new MemoryStream();
                await     img.SaveAsync(ms, new WebpEncoder());
                var bytes = ms.ToArray();

                uploadStream   = new MemoryStream(bytes);
                uploadFileName = Path.GetFileNameWithoutExtension(file.FileName) + ".webp";
            }
            else
            {
                uploadStream   = file.OpenReadStream();
                uploadFileName = file.FileName;
            }

            var uploadParams = new ImageUploadParams
            {
                File           = new FileDescription(uploadFileName, uploadStream),
                Folder         = "virtupath/users/cover",
                Format         = "webp",
                Transformation = new Transformation()
                    .Width(1280).Height(720).Crop("limit")
                    .Quality("auto")
            };

            var result = await _cloud.UploadAsync(uploadParams);

            uploadStream.Dispose();

            if (result.StatusCode != System.Net.HttpStatusCode.OK)
                return StatusCode(500, "Cloudinary upload failed.");

            user.CoverImageUrl      = result.SecureUrl.ToString();
            user.CoverImagePublicId = result.PublicId;
            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                coverImageUrl      = user.CoverImageUrl,
                coverImagePublicId = user.CoverImagePublicId
            });
        }

       [HttpDelete("delete-cover-image")]
        public async Task<IActionResult> DeleteCoverImage([FromQuery] int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            if (!string.IsNullOrEmpty(user.CoverImagePublicId))
            {
                await _cloud.DestroyAsync(new DeletionParams(user.CoverImagePublicId));
                user.CoverImagePublicId = null;
            }

            user.CoverImageUrl = null;
            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Cover image deleted." });
        }


        [HttpPost("bio")]
        public async Task<IActionResult> AddBio([FromBody] TextUpdateRequest req)
        {
            var user = await _context.Users.FindAsync(req.UserId);
            if (user == null) return NotFound("User not found.");

            user.Bio = req.Text;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Bio added." });
        }

        [HttpPut("bio")]
        public async Task<IActionResult> UpdateBio([FromBody] TextUpdateRequest req)
        {
            var user = await _context.Users.FindAsync(req.UserId);
            if (user == null) return NotFound("User not found.");

            user.Bio = req.Text;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Bio updated." });
        }

        [HttpDelete("bio")]
        public async Task<IActionResult> DeleteBio([FromQuery] int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            user.Bio = null;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Bio deleted." });
        }

        [HttpPost("about")]
        public async Task<IActionResult> AddAbout([FromBody] TextUpdateRequest req)
        {
            var user = await _context.Users.FindAsync(req.UserId);
            if (user == null) return NotFound("User not found.");

            user.About = req.Text;
            await _context.SaveChangesAsync();

            return Ok(new { message = "About added." });
        }

        [HttpPut("about")]
        public async Task<IActionResult> UpdateAbout([FromBody] TextUpdateRequest req)
        {
            var user = await _context.Users.FindAsync(req.UserId);
            if (user == null) return NotFound("User not found.");

            user.About = req.Text;
            await _context.SaveChangesAsync();

            return Ok(new { message = "About updated." });
        }

        [HttpDelete("about")]
        public async Task<IActionResult> DeleteAbout([FromQuery] int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            user.About = null;
            await _context.SaveChangesAsync();

            return Ok(new { message = "About deleted." });
        }

        [HttpPut("privacy")]
        public async Task<IActionResult> ToggleProfilePrivacy([FromBody] PrivacyToggleRequest req)
        {
            var user = await _context.Users.FindAsync(req.UserId);
            if (user == null) return NotFound("User not found.");

            user.IsProfilePrivate = req.IsPrivate;
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Profile privacy {(req.IsPrivate ? "enabled" : "disabled")}.", isPrivate = user.IsProfilePrivate });
        }
        [HttpPut("{id}/profile")]
        public async Task<IActionResult> UpdateProfile(int id, [FromBody] UpdateProfileDto dto)
        {
            var u = await _context.Users.FindAsync(id);
            if (u == null) return NotFound();

            u.FullName         = dto.FullName;
            u.Bio              = dto.Bio;
            u.About            = dto.About;
            u.IsProfilePrivate = dto.IsProfilePrivate;

            await _context.SaveChangesAsync();
            return Ok();
        }


        [HttpGet("notifications/{id}")]
        public async Task<IActionResult> GetNotificationSettings(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            return Ok(new
            {
                user.ProductUpdates,
                user.CareerTips,
                user.NewCareerPathAlerts,
                user.Promotions
            });
        }

        [HttpPut("notifications/{id}")]
        public async Task<IActionResult> UpdateNotificationSettings(int id, [FromBody] NotificationSettingsDto settings)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound("User not found.");

            user.ProductUpdates = settings.ProductUpdates;
            user.CareerTips = settings.CareerTips;
            user.NewCareerPathAlerts = settings.NewCareerPathAlerts;
            user.Promotions = settings.Promotions;

            await _context.SaveChangesAsync();

            return Ok(new { message = "Notification settings updated." });
        }

        [HttpPost("set-career")]
public async Task<IActionResult> SetCareerPath([FromBody] SetCareerRequest request)
{
    var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (user == null) return NotFound("User not found.");

    // Always set the career path. Only bump to day 1 if not started yet.
    user.CareerPathID = request.CareerPathId;
    if (user.CurrentDay <= 0) user.CurrentDay = 1;
    user.LastTaskDate = DateTime.UtcNow;

    var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
    if (ip == "::1") ip = "127.0.0.1";
    user.LastKnownIP = ip;
    user.LastActiveAt = DateTime.UtcNow;

    await _context.SaveChangesAsync();
    return Ok("Career path set.");
}


        // ✅ DELETE user (with cleanup of subscriptions)
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            // 1) Remove all subscriptions for this user
            var subs = _context.UserSubscriptions.Where(s => s.UserID == id);
            _context.UserSubscriptions.RemoveRange(subs);

            // 2) Remove all friendship records where this user is either follower or followed
            var friendships = _context.UserFriends
                                      .Where(f => f.FollowerId == id || f.FollowedId == id);
            _context.UserFriends.RemoveRange(friendships);

            // 3) Remove all performance‐review records for this user
            var reviews = _context.PerformanceReviews.Where(r => r.UserID == id);
            _context.PerformanceReviews.RemoveRange(reviews);

            // 4) Now that no FK rows remain, it’s safe to delete the user itself
            _context.Users.Remove(user);

            await _context.SaveChangesAsync();
            return NoContent();
        }


        [HttpPatch("2fa")]
        public async Task<IActionResult> SetTwoFactorCode([FromBody] TwoFactorRequest req)
        {
            var identifier = req.Identifier.ToLower();

            var user = await _context.Users.FirstOrDefaultAsync(u =>
                u.Email.ToLower() == identifier || u.Username.ToLower() == identifier);

            if (user == null) return NotFound();

            user.TwoFactorCode = req.Code;
            user.TwoFactorCodeExpiresAt = req.ExpiresAt;

            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }

        [HttpPost("verify-2fa")]
        public async Task<IActionResult> VerifyTwoFactor([FromBody] VerifyTwoFactorRequest req)
        {
            var identifier = req.Identifier.ToLower();

            var user = await _context.Users.FirstOrDefaultAsync(u =>
                u.Email.ToLower() == identifier || u.Username.ToLower() == identifier);

            if (user == null)
                return Unauthorized(new { error = "User not found" });

            if (user.TwoFactorCode != req.Code || user.TwoFactorCodeExpiresAt < DateTime.UtcNow)
            {
                return Unauthorized(new { error = "Invalid or expired 2FA code" });
            }

            HttpContext.Session.SetInt32("UserID", user.UserID);

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (ip == "::1") ip = "127.0.0.1";
            user.LastKnownIP = ip;

                      // Update “last active” timestamp on successful 2FA
            user.LastActiveAt = DateTime.UtcNow;

            user.TwoFactorCode = null;
            user.TwoFactorCodeExpiresAt = null;

            await _context.SaveChangesAsync();

            if (req.RememberMe)
            {
                Response.Cookies.Append(
                    "VirtuPathRemember",
                    user.UserID.ToString(),
                    new CookieOptions
                    {
                        HttpOnly = false,
                        Secure = true,
                        SameSite = SameSiteMode.None,
                        Expires = DateTimeOffset.UtcNow.AddMonths(1),
                    });
            }

            return Ok(new { userID = user.UserID });
        }

        public class TextUpdateRequest
        {
            public int UserId { get; set; }
            public string? Text { get; set; }
        }

        public class TwoFactorRequest
        {
            public string Identifier { get; set; }
            public string Code { get; set; }
            public DateTime ExpiresAt { get; set; } // ✅ used only when saving
        }

        public class VerifyTwoFactorRequest
        {
            public string Identifier { get; set; }
            public string Code { get; set; }
            public bool RememberMe { get; set; }
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            Response.Cookies.Append("VirtuPathRemember", "", new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(-1),
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None
            });

            return Ok();
        }

        // ✅ GET /api/users/me — Get current session user
        [HttpGet("me")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null) return Unauthorized();

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return Unauthorized();

            return Ok(user);
        }

        [HttpPost("verify/{userId}")]
        public async Task<IActionResult> VerifyUser(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsVerified = true;
            user.VerifiedDate = DateTime.UtcNow; // ✅ also record the date
            await _context.SaveChangesAsync();

            return Ok("User marked as verified.");
        }

        [HttpPost("unverify/{userId}")]
        public async Task<IActionResult> UnverifyUser(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsVerified = false;
            user.VerifiedDate = null;
            await _context.SaveChangesAsync();

            return Ok("User unverified.");
        }

        [HttpPost("official/{userId}")]
        public async Task<IActionResult> MakeOfficial(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsOfficial = true;

            // ✅ Also store verified date for official accounts
            user.VerifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok("User marked as official.");
        }

        [HttpPost("unofficial/{userId}")]
        public async Task<IActionResult> RemoveOfficial(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsOfficial = false;

            // Optional: clear verified date too
            user.VerifiedDate = null;

            await _context.SaveChangesAsync();

            return Ok("Official badge removed.");
        }
        // In VirtuPathAPI/Controllers/UsersController.cs

        [HttpGet("stats")]
        public async Task<IActionResult> GetAllUserStats()
        {
            // 1) Load ALL users (no filtering, no pagination)
            var allUsers = await _context.Users.ToListAsync();

            // 2) Load ALL accepted friendships into memory
            var allFriends = await _context.UserFriends
                .Where(f => f.IsAccepted)
                .ToListAsync();

            // 3) Load ALL performance reviews into memory
            var allReviews = await _context.PerformanceReviews.ToListAsync();

            // 4) Build a stats projection for each user, ensuring nobody is skipped
            var result = allUsers
                .Select(u =>
                {
                    // Count how many accepted followers this user has:
                    int followersCount = allFriends.Count(f => f.FollowedId == u.UserID);

                    // Count how many users *this* user is following (accepted):
                    int followingCount = allFriends.Count(f => f.FollowerId == u.UserID);

                    // Find the most recent PerformanceReview for this user (by Year/Month/ReviewID)
                    var latestReview = allReviews
                        .Where(r => r.UserID == u.UserID)
                        .OrderByDescending(r => r.Year)
                        .ThenByDescending(r => r.Month)
                        .ThenByDescending(r => r.ReviewID)
                        .FirstOrDefault();

                    int latestScore = latestReview?.PerformanceScore ?? 0;

                    return new
                    {
                        u.UserID,
                        u.FullName,
                        u.Username,
                        FollowersCount = followersCount,
                        FollowingCount = followingCount,
                        CurrentDay = u.CurrentDay,
                        LatestPerformanceScore = latestScore
                    };
                })
                .ToList(); // materialize the projection

            // 5) Return all user‐stats in one JSON array
            return Ok(result);
        }
      // base64url
static string B64Url(byte[] bytes)
{
    return Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+','-').Replace('/','_');
}

// ----- PEM exporters -----
static string ExportRsaPublicPem(RSA rsa)
{
    var pub = rsa.ExportSubjectPublicKeyInfo(); // PKCS#8 public
    var b64 = Convert.ToBase64String(pub);
    return $"-----BEGIN PUBLIC KEY-----\n{Chunk64(b64)}\n-----END PUBLIC KEY-----\n";
}
static string ExportRsaPrivatePkcs8Pem(RSA rsa)
{
    var pk = rsa.ExportPkcs8PrivateKey();
    var b64 = Convert.ToBase64String(pk);
    return $"-----BEGIN PRIVATE KEY-----\n{Chunk64(b64)}\n-----END PRIVATE KEY-----\n";
}
static string Chunk64(string s)
{
    var sb = new StringBuilder(s.Length + s.Length/64*2);
    for (int i=0; i<s.Length; i+=64) sb.AppendLine(s.Substring(i, Math.Min(64, s.Length - i)));
    return sb.ToString().TrimEnd();
}

// PUBLIC-ONLY RSA JWK
static string BuildRsaPublicJwk(RSA rsa)
{
    var p = rsa.ExportParameters(false);
    return System.Text.Json.JsonSerializer.Serialize(new {
        kty = "RSA",
        n   = B64Url(p.Modulus!),
        e   = B64Url(p.Exponent!),
        key_ops = new[] { "encrypt" }
    });
}

// X25519 keypair (public JWK, private raw)
static (string publicJwk, byte[] privRaw) GenerateX25519Pair()
{
    var gen = new X25519KeyPairGenerator();
    gen.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new SecureRandom(), 255));
    var kp = gen.GenerateKeyPair();

    var priv = ((X25519PrivateKeyParameters)kp.Private).GetEncoded(); // 32 bytes
    var pub  = ((X25519PublicKeyParameters) kp.Public).GetEncoded();  // 32 bytes

    var jwk = System.Text.Json.JsonSerializer.Serialize(new {
        kty = "OKP",
        crv = "X25519",
        x   = B64Url(pub),
        key_ops = new[] { "deriveKey" }
    });
    return (jwk, priv);
}

// protect -> base64 string (so it fits nvarchar(max))
string ProtectToB64(string plaintext)
{
    var bytes = Encoding.UTF8.GetBytes(plaintext);
    var sealedBytes = _protector.Protect(bytes);
    return Convert.ToBase64String(sealedBytes);
}

        [HttpGet("debug-all-users")]
        public async Task<IActionResult> DebugAllUsers()
        {
            var all = await _context.Users.ToListAsync();
            return Ok(new
            {
                TotalFromUsersDb = all.Count,
                Users = all.Select(u => new { u.UserID, u.Username }).ToList()
            });
        }


        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
        {
            var userId = HttpContext.Session.GetInt32("UserID");
            if (userId == null)
                return Unauthorized(new { error = "User not logged in" });

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return Unauthorized(new { error = "User not found" });

            // Verify old password
            if (!BCrypt.Net.BCrypt.Verify(req.OldPassword, user.PasswordHash))
                return BadRequest(new { error = "Current password is incorrect" });

            // Validate new password
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8 ||
                !Regex.IsMatch(req.NewPassword, @"[A-Z]") ||
                !Regex.IsMatch(req.NewPassword, @"[0-9]") ||
                !Regex.IsMatch(req.NewPassword, @"[!@#$%^&*()_+{}\[\]:;'"",.<>/?\\|`~\-]"))
            {
                return BadRequest(new { error = "Password must be 8+ characters with uppercase, number, and symbol" });
            }

            // Save new password
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }
        [HttpPut("admin/{id}")]
        public async Task<IActionResult> AdminUpdateUser(int id, [FromBody] UpdateUserDto dto)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            // Overwrite only the fields admins can change:
            user.FullName = dto.FullName;
            user.Username = dto.Username.Trim().ToLower();
            user.Email = dto.Email.Trim().ToLower();
            user.IsOfficial = dto.IsOfficial;
            user.IsVerified = dto.IsVerified;

            // Optionally update VerifiedDate when toggling IsVerified:
            if (dto.IsVerified && user.VerifiedDate == null)
            {
                user.VerifiedDate = DateTime.UtcNow;
            }
            else if (!dto.IsVerified)
            {
                user.VerifiedDate = null;
            }

            _context.Users.Update(user);
            await _context.SaveChangesAsync();
            return NoContent();
        }
      private async Task EnsureUserCryptoAsync(User u, bool allowServerPrivate = true)
{
    if (_context.Entry(u).State == EntityState.Detached)
    {
        var tracked = await _context.Users.FindAsync(u.UserID);
        if (tracked != null) u = tracked; else _context.Attach(u);
    }

    var vault = await _context.CobaltUserKeyVaults
        .SingleOrDefaultAsync(v => v.UserId == u.UserID);

    bool userChanged  = false;
    bool vaultChanged = false;

    if (string.IsNullOrEmpty(u.PublicKeyJwk))
    {
        using var rsa = RSA.Create(2048);
        u.PublicKeyJwk = BuildRsaPublicJwk(rsa);
        userChanged = true;

        if (allowServerPrivate)
        {
            var pubPem  = ExportRsaPublicPem(rsa);
            var privPem = ExportRsaPrivatePkcs8Pem(rsa);
            var encPriv = ProtectToB64(privPem);

            if (vault == null)
            {
                vault = new CobaltUserKeyVault
                {
                    UserId        = u.UserID,
                    PubKeyPem     = pubPem,
                    EncPrivKeyPem = encPriv,
                    CreatedAt     = DateTime.UtcNow,
                    IsActive      = true
                };
                _context.CobaltUserKeyVaults.Add(vault);
                vaultChanged = true;
            }
            else
            {
                if (string.IsNullOrEmpty(vault.PubKeyPem))     { vault.PubKeyPem = pubPem;     vaultChanged = true; }
                if (string.IsNullOrEmpty(vault.EncPrivKeyPem)) { vault.EncPrivKeyPem = encPriv; vaultChanged = true; }
            }
        }
    }

    if (userChanged || vaultChanged)
        await _context.SaveChangesAsync();
}


        public class LoginRequest
        {
            public string Identifier { get; set; } // email or username
            public string? Password { get; set; }
            public bool RememberMe { get; set; }
            public bool IsGoogleLogin { get; set; } // NEW

            [JsonPropertyName("clientPublicIp")]
            public string? ClientPublicIp { get; set; }
        }

        public class BanIpRequest { public string Ip { get; set; } }

        public class ChangePasswordRequest
        {
            public string OldPassword { get; set; }
            public string NewPassword { get; set; }
        }

        public class PrivacyToggleRequest
        {
            public int UserId { get; set; }
            public bool IsPrivate { get; set; }
        }

        public class NotificationSettingsDto
        {
            public bool ProductUpdates { get; set; }
            public bool CareerTips { get; set; }
            public bool NewCareerPathAlerts { get; set; }
            public bool Promotions { get; set; }
        }
        public record UpdateProfileDto(string FullName, string? Bio, string? About, bool IsProfilePrivate);
        public class SetCareerRequest
        {
            public string Email { get; set; }
            public int CareerPathId { get; set; }
        }
    }
}
