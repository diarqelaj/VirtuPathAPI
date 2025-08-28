using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace VirtuPathAPI.Models
{
    [Table("UserCareerProgress")]
    public class UserCareerProgress
    {
        // Composite PK configured in OnModelCreating: (UserID, CareerPathID)

        [Required]
        public int UserID { get; set; }

        [Required]
        public int CareerPathID { get; set; }

        // Day numbering starts at 1 (or 0 if you prefer — keep consistent with your tasks)
        [Range(0, int.MaxValue)]
        public int CurrentDay { get; set; } = 1;

        public DateTime? LastTaskDate { get; set; }

        // Navigation (optional)
        [ForeignKey(nameof(UserID))]
        public User? User { get; set; }
    }
}
