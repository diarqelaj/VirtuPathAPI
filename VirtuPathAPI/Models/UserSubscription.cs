using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    public class UserSubscription
    {
        [Key]
        public int SubscriptionID { get; set; }

        public int UserID { get; set; }
        public int CareerPathID { get; set; }
        public DateTime StartDate { get; set; }

        // Computed in DB (do not set in code)
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public DateTime EndDate { get; set; }

        public int LastAccessedDay { get; set; }

        [MaxLength(64)] public string? PaddleTransactionId { get; set; }
        [MaxLength(64)] public string? PaddlePriceId { get; set; }
        [MaxLength(16)] public string? PlanName { get; set; }
        [MaxLength(16)] public string? Billing { get; set; }

    }
}
