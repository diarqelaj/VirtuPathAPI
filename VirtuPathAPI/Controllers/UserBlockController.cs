using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UserBlockController : ControllerBase
{
    private readonly ChatContext _context;

    public UserBlockController(ChatContext context)
    {
        _context = context;
    }

    private int? GetUserId() => HttpContext.Session.GetInt32("UserID");

    [HttpPost("block/{blockedId}")]
    public async Task<IActionResult> BlockUser(int blockedId)
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var already = await _context.UserBlocks.AnyAsync(x => x.BlockerId == me && x.BlockedId == blockedId);
        if (already) return BadRequest("Already blocked");

        _context.UserBlocks.Add(new UserBlock { BlockerId = me.Value, BlockedId = blockedId });
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpDelete("unblock/{blockedId}")]
    public async Task<IActionResult> UnblockUser(int blockedId)
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var block = await _context.UserBlocks.FirstOrDefaultAsync(x => x.BlockerId == me && x.BlockedId == blockedId);
        if (block == null) return NotFound();

        _context.UserBlocks.Remove(block);
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpGet("blocked")]
    public async Task<IActionResult> GetBlockedUsers()
    {
        var me = GetUserId();
        if (me == null) return Unauthorized();

        var list = await _context.UserBlocks
            .Where(x => x.BlockerId == me)
            .Select(x => x.BlockedId)
            .ToListAsync();

        return Ok(list);
    }
}
