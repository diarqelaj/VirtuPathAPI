using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;

namespace VirtuPathAPI.Controllers
{
    [ApiController]
    [Route("sso")]
    public class SsoController : ControllerBase
    {
        private readonly string _htmlPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "wwwroot",
            "sso",
            "login-status.html"
        );

        // 1) Serves HTML and sets the VirtuPathSession cookie
        [HttpGet("login-status")]
        public IActionResult LoginStatus()
        {
            var jwt = GenerateOrRetrieveJwtForCurrentUser();

            Response.Cookies.Append("VirtuPathSession", jwt, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddDays(7)
            });

            if (!System.IO.File.Exists(_htmlPath))
                return NotFound("SSO HTML page not found.");

            return PhysicalFile(_htmlPath, "text/html");
        }

        // 2) Returns the JWT as JSON for postMessage retrieval
        [HttpGet("token")]
        public IActionResult GetToken()
        {
            var jwt = GenerateOrRetrieveJwtForCurrentUser();
            return Ok(new { token = jwt });
        }

        private string GenerateOrRetrieveJwtForCurrentUser()
        {
            // TODO: Replace with your actual JWT generation / validation logic
            return Guid.NewGuid().ToString("N");
        }
    }
}
