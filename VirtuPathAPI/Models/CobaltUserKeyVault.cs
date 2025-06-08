// File: Models/CobaltUserKeyVault.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    public class CobaltUserKeyVault
    {
        // ── 1) Use UserId as *the* PK:
        [Key]
        [Column("UserId")]
        [ForeignKey(nameof(User))]
        public int UserId { get; set; }

        // ── 2) Your payload columns ────────────────────────────────────────
        [Required]
        public string EncPrivKeyPem { get; set; } = null!;

        [Required]
        public string PubKeyPem     { get; set; } = null!;

        // ── 3) Mirror your DB’s timestamps ────────────────────────────────
        public DateTime CreatedAt   { get; set; }
        public DateTime? RotatedAt  { get; set; }
        public bool     IsActive    { get; set; } = true;

        // ── 4) Navigation back to User ────────────────────────────────────
        public User User { get; set; } = null!;
    }
}
