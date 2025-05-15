using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
namespace VirtuPathAPI.Data
{
    
        public class ChatContext : DbContext
        {
            public ChatContext(DbContextOptions<ChatContext> options) : base(options) { }

            public DbSet<Message> Messages { get; set; }
        }
    

}
