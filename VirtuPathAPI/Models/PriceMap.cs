using System.ComponentModel.DataAnnotations;

namespace VirtuPathAPI.Models
{
    public class PriceMap
    {
        [Key]
        [MaxLength(64)]
        public string PaddlePriceId { get; set; } = default!; // e.g., pri_01gsz8x8...

        public int    CareerPathID { get; set; }

        [MaxLength(16)]
        public string PlanName { get; set; } = "starter";     // starter | pro | bonus

        [MaxLength(16)]
        public string Billing  { get; set; } = "monthly";     // monthly | yearly

        public bool   Active   { get; set; } = true;
    }
}
