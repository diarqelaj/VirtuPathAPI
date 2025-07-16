using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using Microsoft.AspNetCore.Http;

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

        [HttpGet("login-status")]
        public IActionResult LoginStatus()
        {
            // 1) Generate or retrieve the JWT for the current user
            var jwt = GenerateOrRetrieveJwtForCurrentUser();

            // 2) Set it as a cookie (no Domain so it defaults to the host serving the HTML)
            Response.Cookies.Append("VirtuPathSession", jwt, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Expires = DateTimeOffset.UtcNow.AddDays(7)
            });

            // 3) Serve the static HTML page which contains your postMessage script
            if (!System.IO.File.Exists(_htmlPath))
                return NotFound("SSO HTML page not found.");

            return PhysicalFile(_htmlPath, "text/html");
        }

        private string GenerateOrRetrieveJwtForCurrentUser()
        {
            // TODO: Replace with your actual JWT generation / validation logic
            return Guid.NewGuid().ToString("N");
        }
    }
}
