// File: Controllers/UserKeyVaultController.cs
using System;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;              // <-- add this
using Microsoft.IdentityModel.Tokens;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/user/vault/keys")]
    public class UserKeyVaultController : ControllerBase
    {
        private readonly UserContext    _db;
        private readonly IDataProtector _vaultProtector;
        private readonly IDataProtector _ratchetProtector;

        // helper so we don't repeat Set<T>() everywhere
        private DbSet<CobaltUserKeyVault> Vaults => _db.Set<CobaltUserKeyVault>();

        public UserKeyVaultController(UserContext db, IDataProtectionProvider dp)
        {
            _db               = db;
            _vaultProtector   = dp.CreateProtector("keyvault"); // for RSA keys
            _ratchetProtector = dp.CreateProtector("ratchet");  // for X25519 blob
        }

        // 1) “ME”
        [HttpGet("me")]
        public async Task<IActionResult> Mine()
        {
            if (!User.Identity!.IsAuthenticated &&
                HttpContext.Session.GetInt32("UserID") is int sid)
            {
                var claims = new[] { new Claim("sub", sid.ToString()) };
                HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            }
            if (!User.Identity!.IsAuthenticated) return Unauthorized();
            if (!int.TryParse(User.FindFirst("sub")?.Value, out var uid)) return Unauthorized();

            var vault = await Vaults.FindAsync(uid);                 // <-- changed
            if (vault is null || !vault.IsActive) return NotFound();

            var user = await _db.Users.FindAsync(uid);
            if (user is null || string.IsNullOrWhiteSpace(user.PublicKeyJwk))
                return NotFound();

            var rsa = JsonNode.Parse(user.PublicKeyJwk)!.AsObject();
            foreach (var f in new[] { "d", "p", "q", "dp", "dq", "qi" })
                rsa.Remove(f);
            rsa["ext"] = true; rsa["use"] = "enc"; rsa["alg"] = "RSA-OAEP-256";

            JsonNode? x25519 = null;
            if (!string.IsNullOrWhiteSpace(vault.X25519PublicJwk))
                x25519 = JsonNode.Parse(vault.X25519PublicJwk);

            string privPem;
            try    { privPem = _vaultProtector.Unprotect(vault.EncPrivKeyPem); }
            catch  { privPem = vault.EncPrivKeyPem; }

            return Ok(new {
                rsaOaepPublicJwk      = rsa,
                x25519PublicJwk       = x25519,
                privateKeyPem         = privPem,
                encRatchetPrivKeyJson = vault.EncRatchetPrivKeyJson
            });
        }

        // 2) ADMIN — by ID
        [HttpGet("{id:int}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> ById(int id)
        {
            var vault = await Vaults.FindAsync(id);                   // <-- changed
            if (vault is null || !vault.IsActive) return NotFound();

            var user = await _db.Users.FindAsync(id);
            if (user is null || string.IsNullOrWhiteSpace(user.PublicKeyJwk))
                return NotFound();

            var rsa = JsonNode.Parse(user.PublicKeyJwk)!.AsObject();
            foreach (var f in new[] { "d", "p", "q", "dp", "dq", "qi" })
                rsa.Remove(f);
            rsa["ext"] = true; rsa["use"] = "enc"; rsa["alg"] = "RSA-OAEP-256";

            JsonNode? x25519 = null;
            if (!string.IsNullOrWhiteSpace(vault.X25519PublicJwk))
                x25519 = JsonNode.Parse(vault.X25519PublicJwk);

            string privPem;
            try    { privPem = _vaultProtector.Unprotect(vault.EncPrivKeyPem); }
            catch  { return StatusCode(500, new { error = "Unable to decrypt RSA key." }); }

            return Ok(new {
                rsaOaepPublicJwk      = rsa,
                x25519PublicJwk       = x25519,
                privateKeyPem         = privPem,
                encRatchetPrivKeyJson = vault.EncRatchetPrivKeyJson
            });
        }

        // 3) PUBLIC — RSA + X25519 *public* keys
        [HttpGet("public-key/{id:int}")]
        [AllowAnonymous]
        public async Task<IActionResult> PublicKey(int id)
        {
            var vault = await Vaults.FindAsync(id);                   // <-- changed
            if (vault is null || !vault.IsActive) return NotFound();
            if (string.IsNullOrWhiteSpace(vault.PubKeyPem)) return NotFound();

            using var rsaAlg = RSA.Create();
            rsaAlg.ImportFromPem(vault.PubKeyPem);
            var p = rsaAlg.ExportParameters(false);

            var rsaJwk = new JsonObject {
                ["kty"] = "RSA",
                ["n"]   = Base64UrlEncoder.Encode(p.Modulus),
                ["e"]   = Base64UrlEncoder.Encode(p.Exponent),
                ["alg"] = "RSA-OAEP-256",
                ["ext"] = true
            };

            JsonNode? x25519 = null;
            if (!string.IsNullOrWhiteSpace(vault.X25519PublicJwk))
                x25519 = JsonNode.Parse(vault.X25519PublicJwk);

            return Ok(new {
                rsaOaepPublicJwk = rsaJwk,
                x25519PublicJwk  = x25519
            });
        }

        // 4) GET your own encrypted ratchet-key blob
        [HttpGet("ratchet-private-key/me")]
        public async Task<IActionResult> GetMyRatchetKey()
        {
            if (!User.Identity!.IsAuthenticated &&
                HttpContext.Session.GetInt32("UserID") is int sid)
            {
                var cs = new[] { new Claim("sub", sid.ToString()) };
                HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(cs, CookieAuthenticationDefaults.AuthenticationScheme));
            }
            if (!User.Identity!.IsAuthenticated) return Unauthorized();
            if (!int.TryParse(User.FindFirst("sub")?.Value, out var meId))
                return Unauthorized();

            var vault = await Vaults.FindAsync(meId);                 // <-- changed
            if (vault is null || !vault.IsActive) return NotFound();

            if (string.IsNullOrWhiteSpace(vault.EncRatchetPrivKeyJson))
                return NoContent();

            string plaintext;
            try    { plaintext = _ratchetProtector.Unprotect(vault.EncRatchetPrivKeyJson); }
            catch  { return StatusCode(500, new { error = "Corrupted ratchet-key blob" }); }

            var doc = JsonSerializer.Deserialize<JsonElement>(plaintext)!;
            return Ok(doc);
        }

        // 5) POST your encrypted ratchet-key blob
        [HttpPost("ratchet-private-key/me")]
        public async Task<IActionResult> SetMyRatchetKey([FromBody] JsonElement blob)
        {
            // restore cookie auth (mirror other endpoints)
            if (!User.Identity!.IsAuthenticated &&
                HttpContext.Session.GetInt32("UserID") is int sid)
            {
                var cs = new[] { new Claim("sub", sid.ToString()) };
                HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(cs, CookieAuthenticationDefaults.AuthenticationScheme));
            }
            if (!User.Identity!.IsAuthenticated) return Unauthorized();
            if (!int.TryParse(User.FindFirst("sub")?.Value, out var meId))
                return Unauthorized();

            var vault = await Vaults.FindAsync(meId);                 // <-- changed
            if (vault is null) return NotFound("You must have an RSA keypair first.");

            vault.EncRatchetPrivKeyJson = _ratchetProtector.Protect(blob.GetRawText());
            vault.RotatedAt             = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return NoContent();
        }

        // 6) (Public) GET any user’s encrypted ratchet blob
        [HttpGet("ratchet-private-key/{id:int}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetRatchetKeyFor(int id)
        {
            var vault = await Vaults.FindAsync(id);                   // <-- changed
            if (vault is null || !vault.IsActive) return NotFound();

            if (string.IsNullOrWhiteSpace(vault.EncRatchetPrivKeyJson))
                return NoContent();

            string plaintext;
            try    { plaintext = _ratchetProtector.Unprotect(vault.EncRatchetPrivKeyJson); }
            catch  { plaintext = vault.EncRatchetPrivKeyJson; }

            var doc = JsonSerializer.Deserialize<JsonElement>(plaintext)!;
            return Ok(doc);
        }
    }
}
