// File: Data/DataProtectionKeyContext.cs
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace VirtuPathAPI.Data
{
    // Note: implements IDataProtectionKeyContext
    public class DataProtectionKeyContext : DbContext, IDataProtectionKeyContext
    {
        public DataProtectionKeyContext(DbContextOptions<DataProtectionKeyContext> options)
            : base(options)
        {
        }

        // This DbSet<T> is required by IDataProtectionKeyContext:
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
    }
}
