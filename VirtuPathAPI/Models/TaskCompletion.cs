using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    [Table("TaskCompletion")]
    public class TaskCompletion
    {
        [Key]
        public int CompletionID { get; set; }

        [Required]
        public int UserID { get; set; }

        [Required]
        public int TaskID { get; set; }

        [Required]
        public DateTime CompletionDate { get; set; }

        // Which logical “day” of that path
        [Required]
        public int CareerDay { get; set; }

        // NEW: scope completions by path
        [Required]
        public int CareerPathID { get; set; }

        // Navigation (optional)
        [ForeignKey(nameof(UserID))]
        public User? User { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        [Column(TypeName = "date")]
        public DateTime? CompletedLocalDate { get; set; } 

        [ForeignKey(nameof(TaskID))]
        public DailyTask? Task { get; set; }
    }
}
