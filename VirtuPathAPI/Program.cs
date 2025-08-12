// File: Program.cs

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using dotenv.net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using VirtuPathAPI.Controllers;
using VirtuPathAPI.Data;
using VirtuPathAPI.Hubs;
using VirtuPathAPI.Models;
using VirtuPathAPI.Utilities;

var builder = WebApplication.CreateBuilder(args);

//────────────────────────────────────────────────────────────────────────────
// 0) CONFIG / ENV / CORS
//────────────────────────────────────────────────────────────────────────────
DotEnv.Load(new DotEnvOptions(probeForEnv: true, ignoreExceptions: true));
// If that still errors, just: DotEnv.Load();

builder.Configuration.AddEnvironmentVariables();

var apiBase = Environment.GetEnvironmentVariable("NEXT_PUBLIC_API_BASE_URL");
string? apiOrigin = null;
if (!string.IsNullOrWhiteSpace(apiBase))
{
    try { apiOrigin = new Uri(apiBase).GetLeftPart(UriPartial.Authority); }
    catch { /* ignore parse errors */ }
}

// Connection string: ENV first, then appsettings.json
var cs = Environment.GetEnvironmentVariable("ConnectionStrings__VirtuPathDB")
         ?? builder.Configuration.GetConnectionString("VirtuPathDB")
         ?? throw new InvalidOperationException("Connection string 'VirtuPathDB' not found.");

Console.WriteLine($"[DB] Using connection string length: {cs.Length}");

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p =>
    {
        var origins = new List<string>
        {
            "https://virtu-path-ai.vercel.app",
            "https://localhost:7072"
        };
        if (!string.IsNullOrEmpty(apiOrigin)) origins.Add(apiOrigin);

        p.WithOrigins(origins.ToArray())
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
            "https://localhost:5249"
        )
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod();
    });
});

//────────────────────────────────────────────────────────────────────────────
// 1) KESTREL / PORT
//────────────────────────────────────────────────────────────────────────────
var portStr = Environment.GetEnvironmentVariable("PORT");
var port = string.IsNullOrWhiteSpace(portStr) ? 8080 : int.Parse(portStr);
builder.WebHost.ConfigureKestrel(o => { o.ListenAnyIP(port); });

//────────────────────────────────────────────────────────────────────────────
// 2) DATABASE CONTEXTS
//────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<DailyTaskContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<DailyQuoteContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<UserContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<UserSubscriptionContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<TaskCompletionContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<PerformanceReviewContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<CareerPathContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<BugReportContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<CommunityPostContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<ChatContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDbContext<DataProtectionKeyContext>(opt => opt.UseSqlServer(cs));

builder.Services.AddDbContext<ReviewContext>(opt => opt.UseSqlServer(cs));

//────────────────────────────────────────────────────────────────────────────
// 3) RSA KEYS & HYBRID ENCRYPTION
//────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<JsonWebKey>(sp =>
{
    var publicPemPath = Path.Combine(builder.Environment.ContentRootPath, "Keys", "rsa_public.pem");
    return RsaKeyLoader.GetRsaPublicJwk(publicPemPath);
});

builder.Services.AddSingleton<RSA>(sp =>
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ChatContext>();
    var vault = db.ServerKeys.SingleOrDefault(k => k.UserId == 1)
                ?? throw new InvalidOperationException("ServerKeys row for UserId=1 not found.");

    var rsa = RSA.Create();
    rsa.ImportFromPem(vault.EncPrivKeyPem);
    return rsa;
});

//────────────────────────────────────────────────────────────────────────────
// 4) SIGNALR PRESENCE
//────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        opts.PayloadSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddSingleton<IUserIdProvider, SessionUserIdProvider>();

//────────────────────────────────────────────────────────────────────────────
// 5) CLOUDINARY
//────────────────────────────────────────────────────────────────────────────
var rawUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL") ?? "";
if (rawUrl.StartsWith("cloudinary://"))
{
    var cloud = new Cloudinary(rawUrl) { Api = { Secure = true } };
    builder.Services.AddSingleton(cloud);
}
else
{
    Console.WriteLine("CLOUDINARY_URL missing or invalid – continuing without Cloudinary.");
}

//────────────────────────────────────────────────────────────────────────────
// 6) SESSION & AUTHENTICATION
//────────────────────────────────────────────────────────────────────────────
builder.Services.AddDistributedMemoryCache();

builder.Services
    .AddDataProtection()
    .SetApplicationName("VirtuPathAPI")
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

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/api/users/login";
        options.Cookie.Name = ".VirtuPath.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("Admin", p => p.RequireClaim(ClaimTypes.Role, "Admin"));
});

//────────────────────────────────────────────────────────────────────────────
// 7) MVC / SWAGGER
//────────────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//────────────────────────────────────────────────────────────────────────────
// 8) BUILD APP & PIPELINE
//────────────────────────────────────────────────────────────────────────────
// ────────────────────────────────────────────────────────────────────────────
// Pipeline
// ────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseStaticFiles();

// Routing FIRST
app.UseRouting();

// Swagger (dev only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS MUST be between UseRouting and auth/endpoints
if (app.Environment.IsDevelopment())
{
    app.UseCors("AllowSwagger");
}
else
{
    app.UseCors("AllowFrontend");
}

// Make cookies usable cross-site (Vercel -> API)
app.UseCookiePolicy(new CookiePolicyOptions
{
    MinimumSameSitePolicy = SameSiteMode.None,
    Secure = CookieSecurePolicy.Always
});

// Session BEFORE auth/endpoints
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

// Remember-me rehydration (kept as-is)
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

// Key-vault seeding (unchanged)
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<UserContext>();
        var dp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("ratchet");

        var toSeed = db.CobaltUserKeyVault
                      .Where(v => string.IsNullOrEmpty(v.EncRatchetPrivKeyJson))
                      .ToList();

        foreach (var vault in toSeed)
        {
            var gen = new X25519KeyPairGenerator();
            gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
            var kp = gen.GenerateKeyPair();
            var privParam = (X25519PrivateKeyParameters)kp.Private;
            var pubParam = (X25519PublicKeyParameters)kp.Public;

            var blob = JsonSerializer.Serialize(new
            {
                priv = Convert.ToBase64String(privParam.GetEncoded()),
                pub  = Convert.ToBase64String(pubParam.GetEncoded())
            });

            vault.EncRatchetPrivKeyJson = protector.Protect(blob);
            vault.RotatedAt = DateTime.UtcNow;
        }

        if (toSeed.Any()) db.SaveChanges();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[KeyVaultSeed] Skipped due to error: {ex.Message}");
    }
}

// Endpoints
app.MapGet("/", () => Results.Ok("API is up"));
app.MapGet("/health", () => Results.Ok("OK"));
app.MapControllers();
app.MapHub<ChatHub>("/chathub");

app.Run();

