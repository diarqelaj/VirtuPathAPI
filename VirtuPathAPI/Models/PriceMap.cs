using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    [Table("PriceMaps")] // <- map to the plural table name in SQL
    public class PriceMap
    {
        [Key]
        [MaxLength(256)]                      // DB: nvarchar(256)
        public string PaddlePriceId { get; set; } = default!; // pri_...

        public int CareerPathID { get; set; }

        [MaxLength(64)]                       // DB: nvarchar(64)
        public string PlanName { get; set; } = "starter";     // starter | pro | bonus

        [MaxLength(64)]                       // DB: nvarchar(64)
        public string Billing  { get; set; } = "monthly";     // monthly | yearly | once

        public bool Active { get; set; } = true;              // DB default ((1))
    }
}
