using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    [Table("PriceMaps", Schema = "dbo")]   // ensure dbo.PriceMaps
    public class PriceMap
    {
        [Key]
        [MaxLength(256)]                   // matches your DB column
        public string PaddlePriceId { get; set; } = default!;

        public int CareerPathID { get; set; }

        [MaxLength(64)]
        public string PlanName { get; set; } = "starter";

        [MaxLength(64)]
        public string Billing  { get; set; } = "monthly";

        public bool Active { get; set; } = true;
    }
}
