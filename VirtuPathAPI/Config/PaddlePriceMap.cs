// File: Config/PaddlePriceMap.cs
using System.Collections.Concurrent;

namespace VirtuPathAPI.Config
{
    public static class PaddlePriceMap
    {
        // price_id -> (careerPathId, plan, billing, termDays)
        // monthly ~ 30 days, yearly = 365. One-time lifetime? set billing="one_time", days=0.
        public static readonly ConcurrentDictionary<string, (int careerPathId, string plan, string billing, int days)> Map
            = new ConcurrentDictionary<string, (int,string,string,int)>();

        // Helper to register in Program.cs if you prefer to keep entries in appsettings
        public static void Add(string priceId, int careerPathId, string plan, string billing, int days)
            => Map[priceId] = (careerPathId, plan, billing, days);
    }
}
