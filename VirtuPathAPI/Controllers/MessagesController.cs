using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Data;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MessagesController : ControllerBase
    {
        private readonly ChatContext _context;

        public MessagesController(ChatContext context)
        {
            _context = context;
        }

        // ✅ GET: /api/messages/fromto?senderId=1&receiverId=2
        [HttpGet("fromto")]
        public async Task<IActionResult> GetMessagesFromSenderToReceiver(int senderId, int receiverId)
        {
            var messages = await _context.Messages
                .Where(m => m.SenderId == senderId && m.ReceiverId == receiverId)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            return Ok(messages);
        }

        // ✅ Get messages between 2 users
        [HttpGet("between")]
        public async Task<IActionResult> GetMessagesBetweenUsers(int user1Id, int user2Id)
        {
            var messages = await _context.Messages
                .Where(m =>
                    (m.SenderId == user1Id && m.ReceiverId == user2Id) ||
                    (m.SenderId == user2Id && m.ReceiverId == user1Id))
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            return Ok(messages);
        }

        // ✅ Post a message
        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] Message message)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                return BadRequest("Message content is empty");

            _context.Messages.Add(message);
            await _context.SaveChangesAsync();

            // You could also inject IHubContext<ChatHub> here and trigger a real-time update

            return Ok(message);
        }
    }
}
