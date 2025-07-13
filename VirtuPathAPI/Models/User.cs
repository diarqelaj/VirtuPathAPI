using System;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtuPathAPI.Models
{
    public class User
    {
        [JsonPropertyName("userID")]
        public int UserID { get; set; }

        [JsonPropertyName("fullName")]
        public string FullName { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; } = null!;

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("passwordHash")]
        public string PasswordHash { get; set; }

        [JsonPropertyName("registrationDate")]
        public DateTime RegistrationDate { get; set; }

        [JsonPropertyName("isVerified")]
        public bool IsVerified { get; set; } = false;

        [JsonPropertyName("verifiedDate")]
        public DateTime? VerifiedDate { get; set; }

        [JsonPropertyName("isOfficial")]
        public bool IsOfficial { get; set; } = false;

        // 2FA fields
        [JsonPropertyName("isTwoFactorEnabled")]
        public bool IsTwoFactorEnabled { get; set; } = false;

        [JsonPropertyName("twoFactorCode")]
        public string? TwoFactorCode { get; set; }

        [JsonPropertyName("twoFactorCodeExpiresAt")]
        public DateTime? TwoFactorCodeExpiresAt { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        // KEEP this public‐key on User, since you want to expose it freely.
        [JsonConverter(typeof(JsonRawStringConverter))]
        [JsonPropertyName("publicKeyJwk")]
        public string? PublicKeyJwk { get; set; }
        // ─────────────────────────────────────────────────────────────────────

        // Profile images
        [JsonPropertyName("profilePictureUrl")]
        public string? ProfilePictureUrl { get; set; }

        [JsonPropertyName("profilePicturePublicId")]
        public string? ProfilePicturePublicId { get; set; }

        [JsonPropertyName("coverImageUrl")]
        public string? CoverImageUrl { get; set; }

        [JsonPropertyName("coverImagePublicId")]
        public string? CoverImagePublicId { get; set; }

        [JsonPropertyName("bio")]
        public string? Bio { get; set; }

        [JsonPropertyName("about")]
        public string? About { get; set; }

        [JsonPropertyName("isProfilePrivate")]
        public bool IsProfilePrivate { get; set; } = false;

        [JsonPropertyName("productUpdates")]
        public bool ProductUpdates { get; set; } = false;

        [JsonPropertyName("careerTips")]
        public bool CareerTips { get; set; } = false;

        [JsonPropertyName("newCareerPathAlerts")]
        public bool NewCareerPathAlerts { get; set; } = false;

        [JsonPropertyName("promotions")]
        public bool Promotions { get; set; } = false;

        // Progress Tracking
        [JsonPropertyName("careerPathID")]
        public int? CareerPathID { get; set; }

        [JsonPropertyName("currentDay")]
        public int CurrentDay { get; set; } = 0;

        [JsonPropertyName("lastTaskDate")]
        public DateTime? LastTaskDate { get; set; }

        [JsonPropertyName("lastKnownIP")]
        public string? LastKnownIP { get; set; }
        
        [JsonPropertyName("lastActiveAt")]
        public DateTime? LastActiveAt { get; set; }
      
        [JsonPropertyName("careerPath")]
        public CareerPath? CareerPath { get; set; }


        // ─────────────────────────────────────────────────────────────────────
        // NEW: one‐to‐one link to the private‐key vault.
        //      (It holds only the encrypted private‐PEM; PublicKeyJwk stays here.)
        //
      public CobaltUserKeyVault? KeyVault { get; set; }
    }
}
