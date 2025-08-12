

    using System.ComponentModel.DataAnnotations;

    namespace VirtuPathAPI.Models
    {
        public class CreateReviewRequest
        {
            [Required]
            public int UserID { get; set; }

            [Required]
            public int CareerPathID { get; set; }

            [Required]
            [Range(1, 5)]
            public int Rating { get; set; }
        }
    }

