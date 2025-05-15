using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using BCrypt.Net;
using System.Text.RegularExpressions;


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

        // ✅ GET user by ID
        [HttpGet("{id}")]
        public async Task<ActionResult<User>> GetUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();
            return user;
        }
        [HttpGet("search")]
        public async Task<IActionResult> SearchUsersByName([FromQuery] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("Name is required.");

            var matches = await _context.Users
                .Where(u => u.FullName.ToLower().Contains(name.ToLower()))
                .Select(u => new {
                    u.UserID,
                    u.FullName,
                    u.ProfilePictureUrl // <- this is required for frontend to show image
                })
                .ToListAsync();

            return Ok(matches);
        }


        
        // ✅ POST /api/users — Register new user with hashed password
        [HttpPost]
        public async Task<ActionResult<User>> CreateUser(User user)
        {
            // Hash the password before saving
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(user.PasswordHash);
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetUser), new { id = user.UserID }, user);
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

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            // Ensure the folder exists
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "profile-pictures");
            if (!Directory.Exists(uploadsFolder))
                Directory.CreateDirectory(uploadsFolder);

            // Generate unique file name
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(uploadsFolder, fileName);

            // Save file to server
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Set relative URL to serve from frontend
            var imageUrl = $"/profile-pictures/{fileName}";
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

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "cover-images");
            if (!Directory.Exists(uploadsFolder))
                Directory.CreateDirectory(uploadsFolder);

            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(uploadsFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var imageUrl = $"/cover-images/{fileName}";
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
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (user == null) return NotFound();

            user.TwoFactorCode = req.Code;
            user.TwoFactorCodeExpiresAt = req.ExpiresAt;

            await _context.SaveChangesAsync();

            return Ok(new { success = true });
        }
        [HttpPost("verify-2fa")]
        public async Task<IActionResult> VerifyTwoFactor([FromBody] VerifyTwoFactorRequest req)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (user == null) return Unauthorized(new { error = "User not found" });

            // ✅ Proper 2FA check
            if (user.TwoFactorCode != req.Code || user.TwoFactorCodeExpiresAt < DateTime.UtcNow)
            {
                return Unauthorized(new { error = "Invalid or expired 2FA code" });
            }

            HttpContext.Session.SetInt32("UserID", user.UserID);

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (ip == "::1") ip = "127.0.0.1";
            user.LastKnownIP = ip;

            // ✅ Clear 2FA values
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
            public string Email { get; set; }
            public string Code { get; set; }
            public DateTime ExpiresAt { get; set; } // ✅ used only when saving
        }
        public class VerifyTwoFactorRequest
        {
            public string Email { get; set; }
            public string Code { get; set; }
            public bool RememberMe { get; set; }
        }



        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (user == null) return Unauthorized();

            // ✅ Google users can login without password
            if (!string.IsNullOrEmpty(req.Password))
            {
                bool isPasswordValid = BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash);
                if (!isPasswordValid) return Unauthorized();
            }
            else
            {
                // Only allow passwordless if user is Google user (no password hash)
                if (!string.IsNullOrEmpty(user.PasswordHash))
                {
                    return Unauthorized(new { error = "Password required" });
                }
            }

            // ✅ Return early if user has 2FA enabled
            if (user.IsTwoFactorEnabled)
            {
                return Ok(new { requires2FA = true });
            }

            // ✅ Normal login (no 2FA)
            HttpContext.Session.SetInt32("UserID", user.UserID);

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (ip == "::1") ip = "127.0.0.1";
            user.LastKnownIP = ip;
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
        public string Email { get; set; }
        public string Password { get; set; }
        public bool RememberMe { get; set; }
    }
    public class ChangePasswordRequest
{
    public string OldPassword { get; set; }
    public string NewPassword { get; set; }
}

}
