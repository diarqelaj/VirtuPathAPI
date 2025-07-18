﻿// Program.cs – VirtuPathAPI
//------------------------------------------------------------
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using VirtuPathAPI.Models;
using VirtuPathAPI.Hubs;
using VirtuPathAPI.Data;
using Microsoft.AspNetCore.SignalR;
using VirtuPathAPI.Controllers;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using dotenv.net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

// ← NEW IMPORTS:
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using VirtuPathAPI.Utilities; // for RsaKeyLoader
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

//------------------------------------------------------------
// 1) DATABASE CONTEXTS
//------------------------------------------------------------
string cs = builder.Configuration.GetConnectionString("VirtuPathDB");

builder.Services.AddDbContext<DailyTaskContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<DailyQuoteContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<UserContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<UserSubscriptionContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<TaskCompletionContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<PerformanceReviewContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<CareerPathContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<BugReportContext>(opt => opt.UseSqlServer(cs));
// Add CommunityPostContext (new)
builder.Services.AddDbContext<CommunityPostContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("VirtuPathDB")));


// Your ChatContext
builder.Services.AddDbContext<ChatContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("VirtuPathDB")));

//------------------------------------------------------------
// 2) LOAD RSA KEYS FOR HYBRID ENCRYPTION
//------------------------------------------------------------
// 2.1) Register the server’s RSA public key as a JsonWebKey (JWK) for clients
builder.Services.AddSingleton<JsonWebKey>(sp =>
{
    // Assumes you put rsa_public.pem in {ContentRoot}/Keys/
    var publicPemPath = Path.Combine(builder.Environment.ContentRootPath, "Keys", "rsa_public.pem");
    return RsaKeyLoader.GetRsaPublicJwk(publicPemPath);
});

builder.Services.AddSingleton<RSA>(sp =>
{
    using var scope = sp.CreateScope();
    var db    = scope.ServiceProvider.GetRequiredService<ChatContext>();
    var vault = db.ServerKeys.Single(k => k.UserId == 1);
    var rsa   = RSA.Create();
    rsa.ImportFromPem(vault.EncPrivKeyPem);   // or vault.PubKeyPem, whichever is private
    return rsa;
});


//------------------------------------------------------------
// 3) SIGNALR
//------------------------------------------------------------
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.AddHttpContextAccessor();
builder.Services
  .AddSignalR()
  .AddJsonProtocol(options => {
    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.DictionaryKeyPolicy  = JsonNamingPolicy.CamelCase;
  });

// Hook SignalR’s UserIdentifier to our session “UserID”
builder.Services.AddSingleton<IUserIdProvider, SessionUserIdProvider>();

//------------------------------------------------------------
// 4) CLOUDINARY (unchanged)
//------------------------------------------------------------
DotEnv.Load(options: new DotEnvOptions(probeForEnv: true));
var rawUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL")?.Trim();
var cloudinary = new Cloudinary(rawUrl);
cloudinary.Api.Secure = true;
builder.Services.AddSingleton(cloudinary);

//------------------------------------------------------------
// 5) CORS POLICIES (unchanged)
//------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p =>
    {
        p.WithOrigins(
            "https://virtu-path-ai.vercel.app",
            "https://virtupathapi-54vt.onrender.com", 
            "https://localhost:7072"
        )
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod();
    });

    options.AddPolicy("AllowSwagger", p =>
    {
        p.WithOrigins(
            "https://localhost:7072",
            "https://localhost:3000",
            "http://localhost:3000",
            "http://localhost:5249"
        )
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

//------------------------------------------------------------
// 6) SESSION + COOKIE POLICY
//------------------------------------------------------------
builder.Services.AddDbContext<DataProtectionKeyContext>(opts =>
    opts.UseSqlServer(builder.Configuration.GetConnectionString("VirtuPathDB")));
builder.Services.AddDistributedMemoryCache();

builder.Services
  .AddDataProtection()
  .SetApplicationName("VirtuPathAPI")               // must be stable across deployments
  .PersistKeysToDbContext<DataProtectionKeyContext>();
builder.Services.AddSession(opt =>
{
    opt.Cookie.Name = ".VirtuPath.Session";
    opt.Cookie.HttpOnly = true;
    opt.Cookie.IsEssential = true;
    opt.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opt.Cookie.SameSite = SameSiteMode.None;
    opt.IdleTimeout = TimeSpan.FromMinutes(360);
});

// ─── 6.1) COOKIE-BASED AUTHENTICATION & AUTHORIZATION ─────────────────
builder.Services
  .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
  .AddCookie(options =>
  {
      options.LoginPath       = "/api/users/login";    // API will return 401, not a redirect
      options.Cookie.Name     = ".VirtuPath.Auth";
      options.Cookie.HttpOnly = true;
      options.Cookie.SameSite = SameSiteMode.None;
      options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

      // Return 401 instead of redirect when unauthorized
      options.Events.OnRedirectToLogin = context =>
      {
          context.Response.StatusCode = StatusCodes.Status401Unauthorized;
          return Task.CompletedTask;
      };
      options.Events.OnRedirectToAccessDenied = context =>
      {
          context.Response.StatusCode = StatusCodes.Status403Forbidden;
          return Task.CompletedTask;
      };
  });

builder.Services.AddAuthorization(options =>
{
    // Example "Admin" policy
    options.AddPolicy("Admin", policy =>
        policy.RequireClaim(ClaimTypes.Role, "Admin"));
});

//------------------------------------------------------------
// 7) MVC / JSON / SWAGGER
//------------------------------------------------------------
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//------------------------------------------------------------
// 8) BUILD
//------------------------------------------------------------
var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions {
  ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});


//------------------------------------------------------------
// 9) PIPELINE
//------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("AllowSwagger");
}
else
{
    app.UseCors("AllowFrontend");
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseCookiePolicy();
app.UseSession();

// ✅ Remember-me Rehydration Middleware
app.Use(async (ctx, next) =>
{
    const string rememberCookie = "VirtuPathRemember";

    if (ctx.Session.GetInt32("UserID") == null &&
        ctx.Request.Cookies.TryGetValue(rememberCookie, out var raw) &&
        int.TryParse(raw, out var uid))
    {
        ctx.Session.SetInt32("UserID", uid);
    }

    await next();
});
using (var scope = app.Services.CreateScope())
{
    var db        = scope.ServiceProvider.GetRequiredService<UserContext>();
    var dp        = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
    var protector = dp.CreateProtector("ratchet");

    // find any users missing an X25519 blob
    var toSeed = db.CobaltUserKeyVault
                   .Where(v => string.IsNullOrEmpty(v.EncRatchetPrivKeyJson))
                   .ToList();

    foreach (var vault in toSeed)
    {
        // 1) generate an X25519 keypair with BouncyCastle
        var gen = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var kp  = gen.GenerateKeyPair();
        var privParam = (X25519PrivateKeyParameters)kp.Private;
        var pubParam  = (X25519PublicKeyParameters)kp.Public;

        var privBytes = privParam.GetEncoded();  // 32 bytes
        var pubBytes  = pubParam.GetEncoded();   // 32 bytes

        // 2) serialize & protect
        var blob = JsonSerializer.Serialize(new {
            priv = Convert.ToBase64String(privBytes),
            pub  = Convert.ToBase64String(pubBytes)
        });
        vault.EncRatchetPrivKeyJson = protector.Protect(blob);
        vault.RotatedAt            = DateTime.UtcNow;
    }

    if (toSeed.Any())
        db.SaveChanges();
}

// ─── NEW: Enable authentication before authorization ───────────────────
app.UseAuthentication();
app.UseAuthorization();


app.MapControllers();

// …and wire up SignalR here:
app.MapHub<ChatHub>("/chathub"); // ✅ SignalR endpoint

app.Run();