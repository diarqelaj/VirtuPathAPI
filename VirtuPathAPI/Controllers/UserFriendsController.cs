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

        // ✅ Send a follow request (not accepted yet)
        [HttpPost("follow")]
        public async Task<IActionResult> FollowUser([FromQuery] int followerId, [FromQuery] int followedId)

        {
            if (followerId == followedId)
                return BadRequest("You cannot follow yourself.");

            bool exists = await _context.UserFriends.AnyAsync(f =>
                f.FollowerId == followerId && f.FollowedId == followedId);

            if (exists)
                return BadRequest("Follow request already exists.");

            _context.UserFriends.Add(new UserFriend
            {
                FollowerId = followerId,
                FollowedId = followedId,
                IsAccepted = false
            });

            await _context.SaveChangesAsync();
            return Ok("Follow request sent.");
        }

        // ✅ Accept a follow request
        [HttpPost("accept")]
        public async Task<IActionResult> AcceptFollow([FromQuery] int followerId, [FromQuery] int followedId)
        {
            var request = await _context.UserFriends.FirstOrDefaultAsync(f =>
                f.FollowerId == followerId && f.FollowedId == followedId);

            if (request == null)
                return NotFound("Follow request not found.");

            request.IsAccepted = true;
            await _context.SaveChangesAsync();

            return Ok("Follow request accepted.");
        }

        // ✅ Remove a follow (unfollow or reject)
        [HttpDelete("remove")]
        public async Task<IActionResult> RemoveFollow([FromQuery] int followerId, [FromQuery] int followedId)
        {
            var relation = await _context.UserFriends.FirstOrDefaultAsync(f =>
                f.FollowerId == followerId && f.FollowedId == followedId);

            if (relation != null)
            {
                _context.UserFriends.Remove(relation);
                await _context.SaveChangesAsync();
                return Ok("Follow removed.");
            }

            return NotFound("No follow relationship found.");
        }

        // ✅ Get list of users this user follows (following list)
        [HttpGet("following/{userId}")]
        public async Task<IActionResult> GetFollowing(int userId)
        {
            var following = await _context.UserFriends
                .Where(f => f.FollowerId == userId && f.IsAccepted)
                .Select(f => f.FollowedId)
                .ToListAsync();

            return Ok(following);
        }

        // ✅ Get list of users who follow this user
        [HttpGet("followers/{userId}")]
        public async Task<IActionResult> GetFollowers(int userId)
        {
            var followers = await _context.UserFriends
                .Where(f => f.FollowedId == userId && f.IsAccepted)
                .Select(f => f.FollowerId)
                .ToListAsync();

            return Ok(followers);
        }
                // ✅ List pending follow requests sent TO this user
        [HttpGet("requests/incoming/{userId}")]
        public async Task<IActionResult> GetIncomingFollowRequests(int userId)
        {
            var incoming = await _context.UserFriends
                .Where(f => f.FollowedId == userId && !f.IsAccepted)
                .ToListAsync();

            return Ok(incoming);
        }

        // ✅ Get mutual friends (real friends)
        [HttpGet("mutual/{userId}")]
        public async Task<IActionResult> GetMutualFriends(int userId)
        {
            var acceptedFollows = await _context.UserFriends
                .Where(f => f.FollowerId == userId && f.IsAccepted)
                .Select(f => f.FollowedId)
                .ToListAsync();

            var mutuals = await _context.UserFriends
                .Where(f =>
                    acceptedFollows.Contains(f.FollowerId) &&
                    f.FollowedId == userId &&
                    f.IsAccepted)
                .Select(f => f.FollowerId)
                .ToListAsync();

            return Ok(mutuals);
        }
    }
}
