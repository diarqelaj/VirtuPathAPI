// File: Controllers/LeaderboardController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace VirtuPathAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LeaderboardController : ControllerBase
    {
        private readonly UserContext _context;

        public LeaderboardController(UserContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<LeaderboardEntryDto>>> GetLeaderboard()
        {
            // 1. Group PerformanceReviews by UserID, compute average PerformanceScore per user:
            var rankings = await _context.PerformanceReviews
                .GroupBy(pr => pr.UserID)
                .Select(g => new
                {
                    UserID = g.Key,
                    AverageScore = g.Average(pr => pr.PerformanceScore)
                })
                .OrderByDescending(x => x.AverageScore)
                .ToListAsync();

            // 2. Fetch User records for those UserIDs:
            var userIds = rankings.Select(r => r.UserID).ToList();
            var users = await _context.Users
                .Where(u => userIds.Contains(u.UserID))
                .ToListAsync();

            // 3. Assemble DTOs with rank, user info, and average score:
            var result = rankings.Select((r, idx) =>
            {
                var user = users.First(u => u.UserID == r.UserID);
                return new LeaderboardEntryDto
                {
                    Rank = idx + 1,
                    UserID = user.UserID,
                    FullName = user.FullName,
                    Username = user.Username,
                    AverageScore = r.AverageScore
                };
            }).ToList();

            return Ok(result);
        }
    }

    public class LeaderboardEntryDto
    {
        public int Rank { get; set; }
        public int UserID { get; set; }
        public string FullName { get; set; } = null!;
        public string Username { get; set; } = null!;
        public double AverageScore { get; set; }
    }
}
