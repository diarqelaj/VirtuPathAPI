using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers;
[ApiController]
[Route("api/[controller]")]
public class UserPinController : ControllerBase
{
    private readonly ChatContext _context;

    public UserPinController(ChatContext context)
    {
        _context = context;
    }

    private int? GetUserId() => HttpContext.Session.GetInt32("UserID");

    [HttpPost("pin/{targetUserId}")]
    public async Task<IActionResult> PinUser(int targetUserId)
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var exists = await _context.UserPins.AnyAsync(x => x.UserId == me && x.PinnedUserId == targetUserId);
        if (exists) return BadRequest("Already pinned");

        _context.UserPins.Add(new UserPin { UserId = me.Value, PinnedUserId = targetUserId });
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpDelete("unpin/{targetUserId}")]
    public async Task<IActionResult> UnpinUser(int targetUserId)
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var pin = await _context.UserPins.FirstOrDefaultAsync(x => x.UserId == me && x.PinnedUserId == targetUserId);
        if (pin == null) return NotFound();

        _context.UserPins.Remove(pin);
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpGet("pinned")]
    public async Task<IActionResult> GetPinnedUsers()
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var list = await _context.UserPins
            .Where(x => x.UserId == me)
            .Select(x => x.PinnedUserId)
            .ToListAsync();

        return Ok(list);
    }
}
