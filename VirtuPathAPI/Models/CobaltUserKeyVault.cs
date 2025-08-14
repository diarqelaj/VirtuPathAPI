// File: Models/CobaltUserKeyVault.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace VirtuPathAPI.Models
{
    public class CobaltUserKeyVault
{
    [Key]
    [Column("UserId")]
    [ForeignKey(nameof(User))]
    public int UserId { get; set; }

    [Required] public string EncPrivKeyPem { get; set; } = null!;
    [Required] public string PubKeyPem     { get; set; } = null!;
    public string? EncRatchetPrivKeyJson   { get; set; }
    public string? X25519PublicJwk         { get; set; }
    public DateTime CreatedAt              { get; set; }
    public DateTime? RotatedAt             { get; set; }
    public bool IsActive                   { get; set; } = true;

    [JsonIgnore] // avoid cycles if you ever serialize the vault
    public User User { get; set; } = null!;
}
}
