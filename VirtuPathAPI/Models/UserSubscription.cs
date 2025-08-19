using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    [Table("UserSubscriptions", Schema = "dbo")]
    public class UserSubscription
    {
        [Key]
        public int Id { get; set; }                 

        public int UserID { get; set; }
        public int CareerPathID { get; set; }

   
        [Required, Column("Plan"), MaxLength(20)]
        public string Plan { get; set; } = "pro";         

        [Required, MaxLength(20)]
        public string Billing { get; set; } = "monthly";   

        [MaxLength(100)]
        public string? PaddleSubscriptionId { get; set; }   

        [MaxLength(100)]
        public string? LastTransactionId { get; set; }    

        [Column(TypeName = "datetime2")]
        public DateTime StartAt { get; set; }            

        [Column(TypeName = "datetime2")]
        public DateTime? CurrentPeriodEnd { get; set; }    

        public bool IsActive { get; set; } = true;
        public bool IsCanceled { get; set; } = false;

        [Column(TypeName = "datetime2")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column(TypeName = "datetime2")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
