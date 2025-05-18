using Microsoft.EntityFrameworkCore;
using VirtuPathAPI.Models;
namespace VirtuPathAPI.Data
{
    
        public class ChattContext : DbContext
        {
            public ChattContext(DbContextOptions<ChattContext> options) : base(options) { }

            public DbSet<Message> Messages { get; set; }
        }
    

}
