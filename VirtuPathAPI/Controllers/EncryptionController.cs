using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using VirtuPathAPI.Models;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
[Route("api/encryption")]
public class EncryptionController : ControllerBase
{
    private readonly UserContext _context;
    public EncryptionController(UserContext context)
    {
        _context = context;
    }

    [HttpGet("public-key/{userId}")]
public async Task<IActionResult> GetPublicKey(int userId)
{
    var user = await _context.Users.FindAsync(userId);
    if (user == null || string.IsNullOrWhiteSpace(user.PublicKeyJwk))
        return NotFound(new { error = "Public key not found" });

    // Parse stored JWK
    var root = JsonNode.Parse(user.PublicKeyJwk)!.AsObject();

    // Remove every private-field if present
    foreach (var field in new[] { "d", "p", "q", "dp", "dq", "qi" })
        root.Remove(field);

    // Optionally keep only the minimal public set:
    // var minimal = new JsonObject {
    //   ["kty"] = root["kty"],
    //   ["n"]   = root["n"],
    //   ["e"]   = root["e"],
    //   ["alg"] = "RSA-OAEP-256",
    //   ["use"] = "enc",
    //   ["ext"] = true
    // };
    // return Ok(minimal);

    // Ensure ext = true so browsers can import it
    root["ext"] = true;
    root["use"] = "enc";
    root["alg"] = "RSA-OAEP-256";

    return Ok(root);
}
}

}
