using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserFriendsController : ControllerBase
    {
        private readonly UserContext _context;

        public UserFriendsController(UserContext context)
        {
            _context = context;
        }

        // ✅ Add friendship (2-way)
        [HttpPost("add")]
        public async Task<IActionResult> AddFriendship(int userId, int friendId)
        {
            // Check if already friends
            bool exists = await _context.UserFriends.AnyAsync(f =>
                f.UserId == userId && f.FriendId == friendId);

            if (exists)
                return BadRequest("Already friends.");

            _context.UserFriends.Add(new UserFriend { UserId = userId, FriendId = friendId });
            _context.UserFriends.Add(new UserFriend { UserId = friendId, FriendId = userId });

            await _context.SaveChangesAsync();
            return Ok("Friendship added.");
        }

        // ✅ Get all friends for a user
        [HttpGet("{userId}")]
        public async Task<IActionResult> GetFriends(int userId)
        {
            var friendIds = await _context.UserFriends
                .Where(f => f.UserId == userId)
                .Select(f => f.FriendId)
                .ToListAsync();

            return Ok(friendIds); // or fetch full user data if needed
        }

        // ✅ Remove friendship (2-way)
        [HttpDelete("remove")]
        public async Task<IActionResult> RemoveFriendship(int userId, int friendId)
        {
            var f1 = await _context.UserFriends.FirstOrDefaultAsync(f => f.UserId == userId && f.FriendId == friendId);
            var f2 = await _context.UserFriends.FirstOrDefaultAsync(f => f.UserId == friendId && f.FriendId == userId);

            if (f1 != null) _context.UserFriends.Remove(f1);
            if (f2 != null) _context.UserFriends.Remove(f2);

            await _context.SaveChangesAsync();
            return Ok("Friendship removed.");
        }
    }
}
