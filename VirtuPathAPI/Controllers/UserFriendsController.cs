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

        // ✅ Send a follow request
        [HttpPost("follow")]
        public async Task<IActionResult> FollowUser([FromQuery] int followerId, [FromQuery] int followedId)
        {
            if (followerId == followedId)
                return BadRequest("You cannot follow yourself.");

            bool exists = await _context.UserFriends.AnyAsync(f =>
                f.FollowerId == followerId && f.FollowedId == followedId);

            if (exists)
                return BadRequest("Follow request already exists.");

            var followedUser = await _context.Users.FindAsync(followedId);
            if (followedUser == null)
                return NotFound("User to follow not found.");

            bool autoAccept = !followedUser.IsProfilePrivate;

            _context.UserFriends.Add(new UserFriend
            {
                FollowerId = followerId,
                FollowedId = followedId,
                IsAccepted = autoAccept
            });

            await _context.SaveChangesAsync();

            return Ok(autoAccept ? "Followed user." : "Follow request sent.");
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

        // ✅ Remove follow/unfollow/reject
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

        // ✅ Followers (accepted only)
        [HttpGet("followers/{userId}")]
        public async Task<IActionResult> GetFollowers(int userId)
        {
            var followers = await _context.UserFriends
                .Where(f => f.FollowedId == userId && f.IsAccepted)
                .Include(f => f.Follower)
                .Select(f => new {
                    f.Follower.UserID,
                     f.Followed.Username,
                    f.Follower.FullName,
                    f.Follower.ProfilePictureUrl
                })
                .ToListAsync();

            return Ok(followers);
        }

        // ✅ Following (accepted only)
        [HttpGet("following/{userId}")]
        public async Task<IActionResult> GetFollowing(int userId)
        {
            var following = await _context.UserFriends
                .Where(f => f.FollowerId == userId && f.IsAccepted)
                .Include(f => f.Followed)
                .Select(f => new {
                    f.Followed.UserID,
                     f.Followed.Username,
                    f.Followed.FullName,
                    f.Followed.ProfilePictureUrl
                })
                .ToListAsync();

            return Ok(following);
        }

       [HttpGet("requests/incoming/{userId}")]
        public async Task<IActionResult> GetIncomingFollowRequests(int userId)
        {
            var incoming = await _context.UserFriends
                .Where(f => f.FollowedId == userId && !f.IsAccepted)
                .Include(f => f.Follower)
                .Select(f => new {
                    FollowerId = f.Follower.UserID,
                    f.Followed.Username, // ✅ match frontend naming
                    f.Follower.FullName,
                    f.Follower.ProfilePictureUrl
                })
                .ToListAsync();

            return Ok(incoming);
        }


        // ✅ Mutuals (real friends)
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
                .Include(f => f.Follower)
                .Select(f => new {
                    f.Follower.UserID,
                    f.Followed.Username,
                    f.Follower.FullName,
                    f.Follower.ProfilePictureUrl
                })
                .ToListAsync();

            return Ok(mutuals);
        }
    }
}
