using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
namespace VirtuPathAPI.Models
{
    public class ChatMessage
    {
        public int Id         { get; set; }
        public int SenderId   { get; set; }
        public int ReceiverId { get; set; }

        // ── ciphertext (mapped to computed ciphertextB64) ─────────────────
        [Column("ciphertextB64")]
        public string Message { get; set; } = null!;

        // ── IV (mapped to computed ivB64) ────────────────────────────────
        [Column("ivB64")]
        public string Iv      { get; set; } = null!;

        // ── auth‐tag (mapped to computed tagB64) ─────────────────────────
        [Column("tagB64")]
        public string Tag     { get; set; } = null!;

        // ── ratchet header (mapped to computed ratchetPubB64) ────────────
        public string DhPubB64 { get; set; }

        public int PN { get; set; }
        public int N  { get; set; }

        // ── metadata ──────────────────────────────────────────────────────
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IsEdited             { get; set; } = false;
        public bool IsDeletedForSender   { get; set; } = false;
        public bool IsDeletedForReceiver { get; set; } = false;
        public int? ReplyToMessageId     { get; set; }

        public bool           IsDelivered  { get; set; } = false;
        public DateTimeOffset? DeliveredAt { get; set; }
        public bool           IsRead       { get; set; } = false;
        public DateTimeOffset? ReadAt      { get; set; }

        // ── navigation props ─────────────────────────────────────────────
        public User Sender   { get; set; } = null!;
        public User Receiver { get; set; } = null!;
        public ICollection<MessageReaction> Reactions { get; set; }
            = new List<MessageReaction>();
    }
}
