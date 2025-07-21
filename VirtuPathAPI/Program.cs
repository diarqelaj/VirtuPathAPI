using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using VirtuPathAPI.Models;
using VirtuPathAPI.Hubs;
using VirtuPathAPI.Data;
using Microsoft.AspNetCore.SignalR;
using VirtuPathAPI.Controllers;
using CloudinaryDotNet;
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
using VirtuPathAPI.Utilities; 
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using CloudinaryDotNet.Actions;

var builder = WebApplication.CreateBuilder(args);

//────────────────────────────────────────────────────────────────────────────
// 0) CORS POLICIES
//────────────────────────────────────────────────────────────────────────────
var apiBase = Environment.GetEnvironmentVariable("NEXT_PUBLIC_API_BASE_URL");
string? apiOrigin = null;
if (!string.IsNullOrWhiteSpace(apiBase))
{
    try
    {
        apiOrigin = new Uri(apiBase).GetLeftPart(UriPartial.Authority);
    }
    catch { /* ignore parse errors */ }
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p =>
    {
        // required origins
        var origins = new List<string>
        {
            "https://virtu-path-ai.vercel.app",
            "https://localhost:7072"
        };
        if (!string.IsNullOrEmpty(apiOrigin))
            origins.Add(apiOrigin);

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
// 1) Kestrel / PORT
//────────────────────────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080); // HTTP
    options.ListenLocalhost(7072, listenOptions =>
    {
        listenOptions.UseHttps(); // HTTPS
    });
});

//────────────────────────────────────────────────────────────────────────────
// 2) DATABASE CONTEXTS
//────────────────────────────────────────────────────────────────────────────
string cs = builder.Configuration.GetConnectionString("VirtuPathDB");
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
    var db    = scope.ServiceProvider.GetRequiredService<ChatContext>();
    var vault = db.ServerKeys.Single(k => k.UserId == 1);
    var rsa   = RSA.Create();
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
        opts.PayloadSerializerOptions.PropertyNamingPolicy   = JsonNamingPolicy.CamelCase;
        opts.PayloadSerializerOptions.DictionaryKeyPolicy   = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddSingleton<IUserIdProvider, SessionUserIdProvider>();

//────────────────────────────────────────────────────────────────────────────
// 5) CLOUDINARY
//────────────────────────────────────────────────────────────────────────────
DotEnv.Load(new DotEnvOptions(probeForEnv: true));
var rawUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL")?.Trim();
var cloudinary = new Cloudinary(rawUrl) { Api = { Secure = true } };
builder.Services.AddSingleton(cloudinary);

//────────────────────────────────────────────────────────────────────────────
// 6) SESSION & AUTHENTICATION
//────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<DataProtectionKeyContext>(opts =>
    opts.UseSqlServer(cs));
builder.Services.AddDistributedMemoryCache();
builder.Services
    .AddDataProtection()
    .SetApplicationName("VirtuPathAPI")
    .PersistKeysToDbContext<DataProtectionKeyContext>();
builder.Services.AddSession(opt =>
{
    opt.Cookie.Name          = ".VirtuPath.Session";
    opt.Cookie.HttpOnly      = true;
    opt.Cookie.IsEssential   = true;
    opt.Cookie.SecurePolicy  = CookieSecurePolicy.Always;
    opt.Cookie.SameSite      = SameSiteMode.None;
    opt.IdleTimeout          = TimeSpan.FromMinutes(360);
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath                = "/api/users/login";
        options.Cookie.Name              = ".VirtuPath.Auth";
        options.Cookie.HttpOnly          = true;
        options.Cookie.SameSite          = SameSiteMode.None;
        options.Cookie.SecurePolicy      = CookieSecurePolicy.Always;
        options.Events.OnRedirectToLogin = ctx => {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx => {
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
        opts.JsonSerializerOptions.PropertyNamingPolicy         = JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        opts.JsonSerializerOptions.DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//────────────────────────────────────────────────────────────────────────────
// 8) BUILD & PIPELINE
//────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseStaticFiles();
app.UseCookiePolicy();
app.UseSession();

app.UseRouting();

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

app.UseAuthentication();
app.UseAuthorization();

// Remember‑me rehydration
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

// Key‑vault seeding
using (var scope = app.Services.CreateScope())
{
    var db        = scope.ServiceProvider.GetRequiredService<UserContext>();
    var dp        = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
    var protector = dp.CreateProtector("ratchet");

    var toSeed = db.CobaltUserKeyVault
                  .Where(v => string.IsNullOrEmpty(v.EncRatchetPrivKeyJson))
                  .ToList();

    foreach (var vault in toSeed)
    {
        var gen  = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var kp        = gen.GenerateKeyPair();
        var privParam = (X25519PrivateKeyParameters)kp.Private;
        var pubParam  = (X25519PublicKeyParameters)kp.Public;

        var blob = JsonSerializer.Serialize(new {
            priv = Convert.ToBase64String(privParam.GetEncoded()),
            pub  = Convert.ToBase64String(pubParam.GetEncoded())
        });
        vault.EncRatchetPrivKeyJson = protector.Protect(blob);
        vault.RotatedAt            = DateTime.UtcNow;
    }

    if (toSeed.Any())
        db.SaveChanges();
}

app.MapGet("/", () => Results.Ok("API is up"));
app.MapGet("/health", () => Results.Ok("OK"));
app.MapControllers();
app.MapHub<ChatHub>("/chathub");

app.Run();
