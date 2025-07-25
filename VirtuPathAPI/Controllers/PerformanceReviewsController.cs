﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PerformanceReviewsController : ControllerBase
    {
        private readonly PerformanceReviewContext _context;

        public PerformanceReviewsController(PerformanceReviewContext context)
        {
            _context = context;
        }

        // GET: api/PerformanceReviews
        [HttpGet]
        public async Task<ActionResult<IEnumerable<PerformanceReview>>> GetPerformanceReviews()
        {
            return await _context.PerformanceReviews.ToListAsync();
        }

        // GET: api/PerformanceReviews/5
        [HttpGet("{id}")]
        public async Task<ActionResult<PerformanceReview>> GetPerformanceReview(int id)
        {
            var review = await _context.PerformanceReviews.FindAsync(id);
            if (review == null)
                return NotFound();

            return review;
        }

        // POST: api/PerformanceReviews
        [HttpPost]
        public async Task<ActionResult<PerformanceReview>> CreatePerformanceReview(PerformanceReview review)
        {
            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();
            return CreatedAtAction(nameof(GetPerformanceReview), new { id = review.ReviewID }, review);
        }

        // PUT: api/PerformanceReviews/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdatePerformanceReview(int id, PerformanceReview review)
        {
            if (id != review.ReviewID)
                return BadRequest();

            _context.Entry(review).State = EntityState.Modified;
            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE: api/PerformanceReviews/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeletePerformanceReview(int id)
        {
            var review = await _context.PerformanceReviews.FindAsync(id);
            if (review == null)
                return NotFound();

            _context.PerformanceReviews.Remove(review);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpGet("progress/daily")]
        public async Task<IActionResult> GetDailyProgress([FromQuery] int userId, [FromQuery] int day)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            if (user.CareerPathID == null)
               return BadRequest("User does not have a CareerPath assigned.");

             int careerPathId = user.CareerPathID.Value;



            var assignedTasks = await _context.DailyTasks
                .Where(dt => dt.Day == day && dt.CareerPathID == careerPathId)
                .ToListAsync();

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .ToListAsync();

            var completedTasks = assignedTasks
                .Where(t => completedTaskIds.Contains(t.TaskID))
                .ToList();

            int tasksAssigned = assignedTasks.Count;
            int tasksCompleted = completedTasks.Count;
            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            return Ok(new
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Day = day,
                TasksAssigned = tasksAssigned,
                TasksCompleted = tasksCompleted,
                PerformanceScore = performanceScore
            });
        }


        // GET: api/PerformanceReviews/progress/weekly?userId=1
        [HttpGet("progress/weekly")]
        public async Task<IActionResult> GetWeeklyProgress([FromQuery] int userId, [FromQuery] int day)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            if (user.CareerPathID == null)
                return BadRequest("User does not have a CareerPath assigned.");

            int careerPathId = user.CareerPathID.Value;

            // 🧠 Use the 'day' from query to compute the correct week range
            int weekStart = ((day - 1) / 7) * 7 + 1;
            int weekEnd = weekStart + 6;

            var assignedTasks = await _context.DailyTasks
                .Where(dt => dt.CareerPathID == careerPathId && dt.Day >= weekStart && dt.Day <= weekEnd)
                .ToListAsync();

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .ToListAsync();

            var completedTasks = assignedTasks
                .Where(t => completedTaskIds.Contains(t.TaskID))
                .ToList();

            int tasksAssigned = assignedTasks.Count;
            int tasksCompleted = completedTasks.Count;
            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            return Ok(new
            {
                UserID = userId,
                CareerPathID = careerPathId,
                WeekRange = $"Days {weekStart}-{weekEnd}",
                TasksAssigned = tasksAssigned,
                TasksCompleted = tasksCompleted,
                PerformanceScore = performanceScore
            });
        }




        // GET: api/PerformanceReviews/progress/monthly?userId=1
        // GET: api/PerformanceReviews/progress/monthly?userId=1
        [HttpGet("progress/monthly")]
        public async Task<IActionResult> GetMonthlyProgress([FromQuery] int userId)
        {
            // 1. Find the user
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            if (user.CareerPathID == null)
                return BadRequest("User does not have a CareerPath assigned.");

            int careerPathId = user.CareerPathID.Value;

            // 2. Get all tasks for this career path within day 1–30 (assume fixed month range)
            var allTasksInMonth = await _context.DailyTasks
                .Where(dt => dt.CareerPathID == careerPathId && dt.Day >= 1 && dt.Day <= 30)
                .ToListAsync();

            // 3. Get user's completed task IDs
            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .ToListAsync();

            // 4. Determine total assigned and completed tasks for the entire month
            var completedTasks = allTasksInMonth
                .Where(t => completedTaskIds.Contains(t.TaskID))
                .ToList();

            int totalAssigned = allTasksInMonth.Count;
            int totalCompleted = completedTasks.Count;
            int performanceScore = totalAssigned == 0 ? 0 : (int)Math.Round((double)(totalCompleted * 100) / totalAssigned);

            // 5. Build weekly progress breakdown
            var weeklyProgress = new List<object>();
            for (int i = 0; i < 5; i++)
            {
                int startDay = i * 7 + 1;
                int endDay = Math.Min(startDay + 6, 30);

                var weekTasks = allTasksInMonth.Where(t => t.Day >= startDay && t.Day <= endDay).ToList();
                var weekCompleted = weekTasks.Where(t => completedTaskIds.Contains(t.TaskID)).ToList();

                weeklyProgress.Add(new
                {
                    Week = $"Week {i + 1}",
                    Completed = weekCompleted.Count,
                    Total = weekTasks.Count
                });
            }

            // 6. Return structured response
            return Ok(new
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = DateTime.UtcNow.Month,
                Year = DateTime.UtcNow.Year,
                TasksAssigned = totalAssigned,
                TasksCompleted = totalCompleted,
                PerformanceScore = performanceScore,
                WeeklyProgress = weeklyProgress
            });
        }
        [HttpGet("progress/alltime")]
        public async Task<IActionResult> GetAllTimeProgress([FromQuery] int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound("User not found.");

            if (user.CareerPathID == null)
                return BadRequest("User does not have a CareerPath assigned.");

            int careerPathId = user.CareerPathID.Value;

            var allTasks = await _context.DailyTasks
                .Where(dt => dt.CareerPathID == careerPathId && dt.Day >= 1 && dt.Day <= 360)
                .ToListAsync();

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .ToListAsync();

            var monthlyProgress = new List<object>();

            for (int month = 0; month < 12; month++)
            {
                int startDay = month * 30 + 1;
                int endDay = startDay + 29;

                var monthTasks = allTasks
                    .Where(dt => dt.Day >= startDay && dt.Day <= endDay)
                    .ToList();

                var completedTasks = monthTasks
                    .Where(t => completedTaskIds.Contains(t.TaskID))
                    .ToList();

                int tasksAssigned = monthTasks.Count;
                int tasksCompleted = completedTasks.Count;

                monthlyProgress.Add(new
                {
                    Month = $"Month {month + 1}",
                    Days = $"{startDay}-{endDay}",
                    Completed = tasksCompleted,
                    Total = tasksAssigned
                });
            }

            return Ok(new
            {
                UserID = userId,
                CareerPathID = careerPathId,
                MonthlyProgress = monthlyProgress
            });
        }


        // POST: api/PerformanceReviews/generate-by-day?userId=1&day=3
        [HttpPost("generate-by-day")]
        public async Task<IActionResult> GeneratePerformanceReviewByDay([FromQuery] int userId, [FromQuery] int day)
        {
            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            var completedOnDay = await _context.DailyTasks
                .Where(dt => dt.Day == day && completedTaskIds.Contains(dt.TaskID))
                .ToListAsync();

            var careerPathId = completedOnDay.FirstOrDefault()?.CareerPathID ?? 0;

            var assignedOnDay = await _context.DailyTasks
                .Where(dt => dt.Day == day && dt.CareerPathID == careerPathId)
                .ToListAsync();

            int tasksCompleted = completedOnDay.Count;
            int tasksAssigned = assignedOnDay.Count;

            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            var review = new PerformanceReview
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = DateTime.UtcNow.Month,
                Year = DateTime.UtcNow.Year,
                TasksCompleted = tasksCompleted,
                TasksAssigned = tasksAssigned,
                PerformanceScore = performanceScore
            };

            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();

            return Ok(review);
        }

        // POST: api/PerformanceReviews/generate-weekly?userId=1
        [HttpPost("generate-weekly")]
        public async Task<IActionResult> GenerateWeeklyPerformance([FromQuery] int userId)
        {
            var currentMonth = DateTime.UtcNow.Month;
            var currentYear = DateTime.UtcNow.Year;

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            var weeklyTasks = await _context.DailyTasks
                .Where(dt => dt.Day >= 1 && dt.Day <= 7)  // First week (Days 1-7)
                .ToListAsync();

            var careerPathId = weeklyTasks.FirstOrDefault()?.CareerPathID ?? 0;

            var assignedTasks = weeklyTasks.Where(dt => dt.CareerPathID == careerPathId).ToList();
            var completedTasks = assignedTasks.Where(t => completedTaskIds.Contains(t.TaskID)).ToList();

            int tasksAssigned = assignedTasks.Count;
            int tasksCompleted = completedTasks.Count;

            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            var review = new PerformanceReview
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = currentMonth,
                Year = currentYear,
                TasksCompleted = tasksCompleted,
                TasksAssigned = tasksAssigned,
                PerformanceScore = performanceScore
            };

            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();

            return Ok(review);
        }

        [HttpGet("user/{userId}")]
        public async Task<ActionResult<List<PerformanceReview>>> GetReviewsForUser(int userId)
        {
            var reviews = await _context.PerformanceReviews
                .Where(r => r.UserID == userId)
                .ToListAsync();

            if (reviews == null || reviews.Count == 0)
                return NotFound($"No performance reviews found for user {userId}.");

            return Ok(reviews);
        }
        // POST: api/PerformanceReviews/generate-monthly?userId=1
        [HttpPost("generate-monthly")]
        public async Task<IActionResult> GenerateMonthlyPerformance([FromQuery] int userId)
        {
            var currentMonth = DateTime.UtcNow.Month;
            var currentYear = DateTime.UtcNow.Year;

            var completedTaskIds = await _context.TaskCompletions
                .Where(tc => tc.UserID == userId)
                .Select(tc => tc.TaskID)
                .Distinct()
                .ToListAsync();

            var monthlyTasks = await _context.DailyTasks
                .Where(dt => dt.Day >= 1 && dt.Day <= 30)  // For simplicity, assuming 30 days in month
                .ToListAsync();

            var careerPathId = monthlyTasks.FirstOrDefault()?.CareerPathID ?? 0;

            var assignedTasks = monthlyTasks.Where(dt => dt.CareerPathID == careerPathId).ToList();
            var completedTasks = assignedTasks.Where(t => completedTaskIds.Contains(t.TaskID)).ToList();

            int tasksAssigned = assignedTasks.Count;
            int tasksCompleted = completedTasks.Count;

            int performanceScore = tasksAssigned == 0 ? 0 : (int)Math.Round((double)(tasksCompleted * 100) / tasksAssigned);

            var review = new PerformanceReview
            {
                UserID = userId,
                CareerPathID = careerPathId,
                Month = currentMonth,
                Year = currentYear,
                TasksCompleted = tasksCompleted,
                TasksAssigned = tasksAssigned,
                PerformanceScore = performanceScore
            };

            _context.PerformanceReviews.Add(review);
            await _context.SaveChangesAsync();

            return Ok(review);
        }
    }
}
