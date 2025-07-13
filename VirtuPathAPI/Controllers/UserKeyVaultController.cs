using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using VirtuPathAPI.Models;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("api/user/vault/keys")]
    public class UserKeyVaultController : ControllerBase
    {
        private readonly UserContext    _db;
        private readonly IDataProtector _dp;

        public UserKeyVaultController(UserContext db, IDataProtectionProvider dp)
        {
            _db = db;
            // make sure your Program.cs has a persistent key ring and SetApplicationName(...)
            _dp = dp.CreateProtector("keyvault");
        }

        // ─── 1) “ME” – protected by cookie *or* by session fallback ─────────────
        // GET api/user/vault/keys/me
        // GET api/user/vault/keys/me
[HttpGet("me")]
public async Task<IActionResult> Mine()
{
    // 1) If the cookie auth didn't run, try the session:
    if (!User.Identity!.IsAuthenticated)
    {
        var sid = HttpContext.Session.GetInt32("UserID");
        if (sid.HasValue)
        {
            var claims   = new[] { new Claim("sub", sid.Value.ToString()) };
            var identity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme
            );
            HttpContext.User = new ClaimsPrincipal(identity);
        }
    }

    // 2) Still not authed?
    if (!User.Identity.IsAuthenticated)
        return Unauthorized();

    // 3) Pull the user-ID from the "sub" claim
    var sub = User.FindFirst("sub")?.Value;
    if (!int.TryParse(sub, out var uid))
        return Unauthorized();

    // 4) Fetch the active vault row
    var row = await _db.CobaltUserKeyVault.FindAsync(uid);
    if (row == null || !row.IsActive)
        return NotFound();

    // 5) Fetch the user (for public JWK)
    var user = await _db.Users.FindAsync(uid);
    if (user == null || string.IsNullOrWhiteSpace(user.PublicKeyJwk))
        return NotFound();

    // 6) Parse the stored JWK
    var jwkJson = JsonDocument.Parse(user.PublicKeyJwk).RootElement;

    // 7) Try to decrypt the private PEM, but on any failure just return the raw string
    string privatePem;
    try
    {
        privatePem = _dp.Unprotect(row.EncPrivKeyPem);
    }
    catch (CryptographicException)
    {
        privatePem = row.EncPrivKeyPem;
    }

    // 8) Return both pieces
    return Ok(new {
        publicKeyJwk  = jwkJson,
        privateKeyPem = privatePem
    });
}


        // ─── 2) ADMIN “BY ID” ────────────────────────────────────────────────
        // GET api/user/vault/keys/{id}
        [HttpGet("{id:int}"), Microsoft.AspNetCore.Authorization.Authorize(Policy = "Admin")]
        public async Task<IActionResult> ById(int id)
        {
            var row = await _db.CobaltUserKeyVault.FindAsync(id);
            if (row == null || !row.IsActive)
                return NotFound();

            var user = await _db.Users.FindAsync(id);
            if (user == null || string.IsNullOrWhiteSpace(user.PublicKeyJwk))
                return NotFound();

            var jwkJson = JsonDocument.Parse(user.PublicKeyJwk).RootElement;
            string privatePem;
            try
            {
                privatePem = _dp.Unprotect(row.EncPrivKeyPem);
            }
            catch (CryptographicException)
            {
                return StatusCode(500, new { error = "Unable to decrypt vault entry." });
            }

            return Ok(new {
                publicKeyJwk  = jwkJson,
                privateKeyPem = privatePem
            });
        }

        // ─── 3) PUBLIC “ONLY PUBLIC KEY” ────────────────────────────────────
       [HttpGet("public-key/{id:int}")]
public async Task<IActionResult> PublicKey(int id)
{
    var row = await _db.CobaltUserKeyVault.FindAsync(id);
    if (row == null || !row.IsActive) return NotFound();

    // Load RSA from the PEM you persisted
    using var rsa = RSA.Create();
    rsa.ImportFromPem(row.PubKeyPem);

    var p = rsa.ExportParameters(false);
    string n = Base64UrlEncoder.Encode(p.Modulus);
    string e = Base64UrlEncoder.Encode(p.Exponent);

    // Return lower-case JSON exactly as JWK expects
    var jwk = new {
      kty = "RSA",
      n,
      e,
      alg = "RSA-OAEP",
      ext = true
    };

    return new JsonResult(new { publicKeyJwk = jwk });
}
    }
}
