using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using BCrypt.Net;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;



namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly UserContext _context;

        public UsersController(UserContext context)
        {
            _context = context;
        }

        // ✅ GET all users (admin/debug only)
        [HttpGet]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            return await _context.Users.ToListAsync();
        }
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Identifier))
                return BadRequest(new { error = "Email or username is required." });

            var identifier = req.Identifier.Trim().ToLower();

            var user = await _context.Users.FirstOrDefaultAsync(u =>
                u.Email.ToLower() == identifier || u.Username.ToLower() == identifier);

            if (user == null) return Unauthorized(new { error = "User not found." });

            if (!string.IsNullOrWhiteSpace(req.Password))
            {
                if (string.IsNullOrEmpty(user.PasswordHash))
                {
                    return Unauthorized(new { error = "This account uses Google authentication." });
                }

                if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                {
                    return Unauthorized(new { error = "Invalid password." });
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(user.PasswordHash))
                {
                    return Unauthorized(new { error = "Password required for this account." });
                }
            }

            // ✅ If 2FA is enabled
            if (user.IsTwoFactorEnabled)
            {
                return Ok(new { requires2FA = true });
            }

            // ✅ Login successful
            HttpContext.Session.SetInt32("UserID", user.UserID);
            user.LastKnownIP = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            await _context.SaveChangesAsync();

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
            "team", "teams", "help", "faq"
        };
        

        [HttpPost]
        public async Task<ActionResult<User>> CreateUser(User user)
        {
            // ✅ Check required fields
            if (string.IsNullOrWhiteSpace(user.Email) ||
                string.IsNullOrWhiteSpace(user.PasswordHash) ||
                string.IsNullOrWhiteSpace(user.Username))
            {
                return BadRequest("Email, password, and username are required.");
            }

            // ✅ Trim and lowercase the username
            user.Username = user.Username.Trim().ToLower();

            // ✅ Reserved username check
            if (ReservedUsernames.Contains(user.Username))
            {
                return BadRequest("This username is reserved. Please choose another.");
            }

            // ✅ Profanity check (this was missing)
            foreach (var word in ProfanityList.Words)
            {
                if (user.Username.Contains(word))
                {
                    return BadRequest("Username contains inappropriate content.");
                }
            }

            // ✅ Format check
            if (!Regex.IsMatch(user.Username, @"^[a-z0-9_]{3,20}$"))
            {
                return BadRequest("Username must be 3–20 characters and contain only lowercase letters, numbers, or underscores.");
            }

            // ✅ Check for duplicates
            if (await _context.Users.AnyAsync(u => u.Username == user.Username))
                return Conflict(new { error = "Username already taken" });

            if (await _context.Users.AnyAsync(u => u.Email == user.Email))
                return Conflict(new { error = "Email already in use" });

            // ✅ Hash the password
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(user.PasswordHash);
            user.RegistrationDate = DateTime.UtcNow;

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

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
                    u.IsOfficial 
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
                    u.Username, // ✅ ADD THIS
                    u.ProfilePictureUrl,
                    u.IsVerified,
                    u.VerifiedDate,
                    u.IsOfficial
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

            // ✅ Limit file size to 5MB
            if (file.Length > 5 * 1024 * 1024)
                return BadRequest("Image must be less than 5MB.");

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!allowedExtensions.Contains(ext))
                return BadRequest("Only .jpg, .jpeg, .png, and .webp image formats are allowed.");

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            // ✅ Delete old image if exists
            if (!string.IsNullOrEmpty(user.ProfilePictureUrl))
            {
                var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.ProfilePictureUrl.TrimStart('/'));
                if (System.IO.File.Exists(oldPath))
                    System.IO.File.Delete(oldPath);
            }

            // ✅ Ensure folder exists
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "profile-pictures");
            if (!Directory.Exists(uploadsFolder))
                Directory.CreateDirectory(uploadsFolder);

            // ✅ Convert + resize to .webp
            var webpFileName = $"{Guid.NewGuid()}.webp";
            var webpFilePath = Path.Combine(uploadsFolder, webpFileName);

            using (var image = await Image.LoadAsync(file.OpenReadStream()))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(512, 512)
                }));

                await image.SaveAsync(webpFilePath, new WebpEncoder { Quality = 85 });
            }

            // ✅ Update user with new image
            var imageUrl = $"/profile-pictures/{webpFileName}";
            user.ProfilePictureUrl = imageUrl;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { profilePictureUrl = imageUrl });
        }

        [HttpDelete("delete-profile-picture")]
        public async Task<IActionResult> DeleteProfilePicture([FromQuery] int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            if (!string.IsNullOrEmpty(user.ProfilePictureUrl))
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.ProfilePictureUrl.TrimStart('/'));
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
                user.ProfilePictureUrl = null;
                _context.Users.Update(user);
                await _context.SaveChangesAsync();
            }

            return Ok(new { message = "Profile picture deleted." });
        }


        [HttpPost("upload-cover")]
        public async Task<IActionResult> UploadCoverImage([FromForm] IFormFile file, [FromForm] int userId)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            // ✅ Max 5MB check
            if (file.Length > 5 * 1024 * 1024)
                return BadRequest("Image must be less than 5MB.");

            // ✅ Validate allowed image formats
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!allowedExtensions.Contains(ext))
                return BadRequest("Only .jpg, .jpeg, .png, and .webp image formats are allowed.");

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            // ✅ Delete old cover image if it exists
            if (!string.IsNullOrEmpty(user.CoverImageUrl))
            {
                var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.CoverImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(oldPath))
                    System.IO.File.Delete(oldPath);
            }

            // ✅ Ensure folder exists
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "cover-images");
            if (!Directory.Exists(uploadsFolder))
                Directory.CreateDirectory(uploadsFolder);

            // ✅ Convert to .webp and resize (1280x720 max)
            var webpFileName = $"{Guid.NewGuid()}.webp";
            var filePath = Path.Combine(uploadsFolder, webpFileName);

            using (var image = await Image.LoadAsync(file.OpenReadStream()))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(1280, 720)
                }));

                await image.SaveAsync(filePath, new WebpEncoder { Quality = 85 });
            }

            var imageUrl = $"/cover-images/{webpFileName}";
            user.CoverImageUrl = imageUrl;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { coverImageUrl = imageUrl });
        }

        [HttpDelete("delete-cover-image")]
        public async Task<IActionResult> DeleteCoverImage([FromQuery] int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            if (!string.IsNullOrEmpty(user.CoverImageUrl))
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.CoverImageUrl.TrimStart('/'));
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }

                user.CoverImageUrl = null;
                _context.Users.Update(user);
                await _context.SaveChangesAsync();
            }

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

        public class PrivacyToggleRequest
        {
            public int UserId { get; set; }
            public bool IsPrivate { get; set; }
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

        public class NotificationSettingsDto
        {
            public bool ProductUpdates { get; set; }
            public bool CareerTips { get; set; }
            public bool NewCareerPathAlerts { get; set; }
            public bool Promotions { get; set; }
        }


        [HttpPost("set-career")]
        public async Task<IActionResult> SetCareerPath([FromBody] SetCareerRequest request)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
            if (user == null) return NotFound("User not found.");

            // ✅ Only update if day is not already set
            if (user.CurrentDay == 0)
            {
                user.CareerPathID = request.CareerPathId;
                user.CurrentDay = 1;
                user.LastTaskDate = DateTime.UtcNow;

                var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
                if (ip == "::1") ip = "127.0.0.1";
                user.LastKnownIP = ip;

                await _context.SaveChangesAsync();
            }

            return Ok("Career path set.");
            Console.WriteLine("Set-career called");
        }



        // ✅ DELETE user
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

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

    }
     

    public class LoginRequest
    {
        public string Identifier { get; set; } // email or username
        public string? Password { get; set; }
        public bool RememberMe { get; set; }
        public bool IsGoogleLogin { get; set; } // NEW
    }


    public class ChangePasswordRequest
{
    public string OldPassword { get; set; }
    public string NewPassword { get; set; }
}

}
