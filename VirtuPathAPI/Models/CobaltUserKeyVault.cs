// File: Models/CobaltUserKeyVault.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    public class CobaltUserKeyVault
    {
        [Key]
        [Column("UserId")]
        [ForeignKey(nameof(User))]
        public int UserId { get; set; }

        // your existing fields
        [Required]
        public string EncPrivKeyPem   { get; set; } = null!;

        [Required]
        public string PubKeyPem       { get; set; } = null!;

        public string? X25519PublicJwk { get; set; }

        // ← NEW: encrypted ratchet‐key blob
        [Column(TypeName = "nvarchar(max)")]
        public string? EncRatchetPrivKeyJson { get; set; }

        public DateTime CreatedAt   { get; set; }
        public DateTime? RotatedAt  { get; set; }
        public bool     IsActive    { get; set; } = true;

        public User User { get; set; } = null!;
    }
}
