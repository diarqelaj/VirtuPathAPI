using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers;
[ApiController]
[Route("api/[controller]")]
public class UserMuteController : ControllerBase
{
    private readonly ChatContext _context;

    public UserMuteController(ChatContext context)
    {
        _context = context;
    }

    private int? GetUserId() => HttpContext.Session.GetInt32("UserID");

    [HttpPost("mute/{mutedId}")]
    public async Task<IActionResult> MuteUser(int mutedId)
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        if (await _context.UserMutes.AnyAsync(x => x.MuterId == me && x.MutedId == mutedId))
            return BadRequest("Already muted");

        _context.UserMutes.Add(new UserMute { MuterId = me.Value, MutedId = mutedId });
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete("unmute/{mutedId}")]
    public async Task<IActionResult> UnmuteUser(int mutedId)
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var mute = await _context.UserMutes.FirstOrDefaultAsync(x => x.MuterId == me && x.MutedId == mutedId);
        if (mute == null) return NotFound();

        _context.UserMutes.Remove(mute);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("muted")]
    public async Task<IActionResult> GetMutedUsers()
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var list = await _context.UserMutes
            .Where(x => x.MuterId == me)
            .Select(x => x.MutedId)
            .ToListAsync();

        return Ok(list);
    }
}
