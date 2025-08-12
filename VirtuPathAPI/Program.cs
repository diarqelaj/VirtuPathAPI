
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
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using VirtuPathAPI.Utilities; 
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using CloudinaryDotNet.Actions;

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
builder.Services.AddDbContext<ReviewContext>(opt => opt.UseSqlServer(cs));

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
    var db = scope.ServiceProvider.GetRequiredService<ChatContext>();
    var vault = db.ServerKeys.Single(k => k.UserId == 1);
    var rsa = RSA.Create();
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
      options.PayloadSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
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
      options.LoginPath = "/api/users/login";    // API will return 401, not a redirect
      options.Cookie.Name = ".VirtuPath.Auth";
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
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
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

// ─── NEW: Enable authentication before authorization ───────────────────
app.UseAuthentication();
app.UseAuthorization();


app.MapControllers();

// …and wire up SignalR here:
app.MapHub<ChatHub>("/chathub"); // ✅ SignalR endpoint

app.Run();
