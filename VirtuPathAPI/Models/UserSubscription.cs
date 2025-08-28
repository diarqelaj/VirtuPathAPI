using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    [Table("UserSubscriptions", Schema = "dbo")]
    public class UserSubscription
    {
        [Key]
        [Column("SubscriptionID")] // DB column
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; } // EF still uses Id, but it maps to SubscriptionID

        [Column("UserID")]
        public int UserID { get; set; }

        [Column("CareerPathID")]
        public int CareerPathID { get; set; }

        [Required, Column("PlanName"), MaxLength(32)]
        public string Plan { get; set; } = "pro";

        [Required, Column("Billing"), MaxLength(32)]
        public string Billing { get; set; } = "monthly";

        [Column("PaddlePriceId"), MaxLength(128)]
        public string? PaddleSubscriptionId { get; set; }  // maps to PaddlePriceId

        [Column("PaddleTransactionId"), MaxLength(128)]
        public string? LastTransactionId { get; set; }

        [Column("StartDate", TypeName = "date")]
        public DateTime StartAt { get; set; }

        [Column("EndDate", TypeName = "date")]
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public DateTime? CurrentPeriodEnd { get; set; }   // <-- public setter (no CS0272)

        

        // Not in DB: must be either dropped or made shadow properties / defaults
        [NotMapped] public bool IsActive { get; set; } = true;
        [NotMapped] public bool IsCanceled { get; set; } = false;
        [NotMapped] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [NotMapped] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Column("LastAccessedDay")]
        public int? LastAccessedDay { get; set; }
    }
}
