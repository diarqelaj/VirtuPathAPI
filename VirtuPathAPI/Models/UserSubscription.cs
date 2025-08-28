using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    [Table("UserSubscriptions", Schema = "dbo")]
    public class UserSubscription
    {
        [Key]
        [Column("SubscriptionID")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Column("UserID")]
        public int UserID { get; set; }

        [Column("CareerPathID")]
        public int CareerPathID { get; set; }

        [Required, Column("PlanName"), MaxLength(32)]
        public string Plan { get; set; } = "pro";

        [Required, Column("Billing"), MaxLength(32)]
        public string Billing { get; set; } = "monthly";

        // keep the property name used elsewhere, map to DB column
        [Column("PaddlePriceId"), MaxLength(128)]
        public string? PaddleSubscriptionId { get; set; }

        // keep the property name used elsewhere, map to DB column
        [Column("PaddleTransactionId"), MaxLength(128)]
        public string? LastTransactionId { get; set; }

        [Column("StartDate")]                      // (TypeName="date" if your column is DATE)
        public DateTime StartAt { get; set; }

        [Column("EndDate")]
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public DateTime? CurrentPeriodEnd { get; set; }

        [Column("LastAccessedDay")]
        public int? LastAccessedDay { get; set; }

        // These do NOT exist in DB — keep NotMapped so EF won't try to translate them
        [NotMapped] public bool IsActive { get; set; } = true;
        [NotMapped] public bool IsCanceled { get; set; } = false;
        [NotMapped] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [NotMapped] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
