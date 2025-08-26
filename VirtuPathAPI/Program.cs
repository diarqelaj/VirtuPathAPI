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
using System.Collections.Generic;
using VirtuPathAPI.Utilities;
using VirtuPathAPI.Config;
using VirtuPathAPI.Services;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

//────────────────────────────────────────────────────────────────────────────
// 0) CONFIG / ENV / CORS
//────────────────────────────────────────────────────────────────────────────
DotEnv.Load(new DotEnvOptions(probeForEnv: true, ignoreExceptions: true));
builder.Configuration.AddEnvironmentVariables();

// 🔇 Server logging: silence everything outside Development
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.None);

    // (Optional) keep the filters for clarity if you ever re-add providers
    builder.Logging.AddFilter("Microsoft", LogLevel.None);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.None);
    builder.Logging.AddFilter("System", LogLevel.None);
    builder.Logging.AddFilter("Default", LogLevel.None);
}

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

if (builder.Environment.IsDevelopment())
{
    Console.WriteLine($"[DB] Using connection string length: {cs.Length}");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p =>
    {
        var origins = new List<string>
        {
            "https://virtu-path-ai.vercel.app",
            "https://www.virtupathai.com",
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
builder.Services.AddScoped<VirtuPathAPI.Services.EntitlementService>();
builder.Services.AddDbContext<ReviewContext>(opt => opt.UseSqlServer(cs));
builder.Services.AddDataProtection();

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
    if (builder.Environment.IsDevelopment())
    {
        Console.WriteLine("CLOUDINARY_URL missing or invalid – continuing without Cloudinary.");
    }
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
builder.Services.AddSwaggerGen(c =>
{
    // Use full names (and replace '+' for nested types) to avoid schema ID collisions
    c.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace("+", "."));
});

//────────────────────────────────────────────────────────────────────────────
// 8) BUILD APP & PIPELINE
//────────────────────────────────────────────────────────────────────────────
static void RegisterPaddlePriceIds()
{
    // monthly = 30 days, yearly = 365 days
    const int M = 30;
    const int Y = 365;

    // careerId → (monthlyStarter, monthlyPro, monthlyBonus, yearlyStarter, yearlyPro, yearlyBonus)
    var ids = new Dictionary<int, (string ms, string mp, string mb, string ys, string yp, string yb)>
    {
        {  1, ("pri_01k2dd1qw5rrybp9w03e0yh3e5","pri_01k2dd1s2bzbe81tt81smyga6z","pri_01k2dd1t7768557mvpr4thbhs3","pri_01k2dd1vd04b7syn813f65ss9a","pri_01k2dd1wj8wxscdthewzzjycm6","pri_01k2dd1xr323y0dnerd922j1dt") },
        {  2, ("pri_01k2dd22dwc1s76ttf4pjxcmmn","pri_01k2dd23kmz81srn5mn6bhj04v","pri_01k2dd24rxhenh68xw3778qa6p","pri_01k2dd25ysw1d5qn1rpyb5kjc2","pri_01k2dd274bgm8k5p4knhdj53s8","pri_01k2dd28ac6jp0nye66m4nfym3") },
        {  3, ("pri_01k2dd2cztnekth62qbev5hfy7","pri_01k2dd2e52k91198ez3n6dg6wq","pri_01k2dd2faw4fphqarc7p46sv86","pri_01k2dd2gr15b1az6kc3nfs0s85","pri_01k2dd2hnzyk1mtpx1yy1mjczh","pri_01k2dd2jvn8pfxr36pr56ek3pb") },
        {  4, ("pri_01k2dd2qj8te29bg3vsmyhmvme","pri_01k2dd2rptrq7hrtsdxdze4em9","pri_01k2dd2swgc1cfwnbp5am7zpjc","pri_01k2dd2v21gk1kqmngshc9gtvc","pri_01k2dd2w7v6bbcq4326k3bnwm2","pri_01k2dd2xe0b63w69smbg9j63k8") },
        {  5, ("pri_01k2dd3239pchjzg9hyw6c43gq","pri_01k2dd339150790adzm5pwfeb4","pri_01k2dd34egy2r8hcx7djx0a6v4","pri_01k2dd35mmrnqrzvfap9yxb5m3","pri_01k2dd36sn3rsq8qsvgppnd21c","pri_01k2dd37z59cs1b4wk5x43khbv") },
        {  6, ("pri_01k2dd3cndvdnd3kecsdycveqf","pri_01k2dd3dtshgpkpehz2kpjp89y","pri_01k2dd3f0pqdgtq9mpydgsm7ht","pri_01k2dd3g6746xeazar9bkna6tr","pri_01k2dd3hb31ntpm3txp1hc2jmx","pri_01k2dd3jgepvdr6rsvct5pqcs3") },
        {  7, ("pri_01k2dd3q6sxp26f9mtzt2b0xaf","pri_01k2dd3rcapsrcjxa3he22a17a","pri_01k2dd3shyyzkbedmqdrcj4a4g","pri_01k2dd3tq7f3m2rbg6wd4aaef1","pri_01k2dd3vwtva8m5m2jz07wkf58","pri_01k2dd3x2se8wcx4dnmjh9rvxv") },
        {  8, ("pri_01k2dd41rxhrxy1pwt3qhx64sm","pri_01k2dd42ywyfjnwtyg19ykma80","pri_01k2dd443c7eg7vhp75tw9r7e0","pri_01k2dd458w1eshrhdvx6js4q5q","pri_01k2dd46ep4qh9anepknev0s7g","pri_01k2dd47mcs9tny1acvqbhrg6w") },
        {  9, ("pri_01k2dd4ca84far0fn29yrh5h3q","pri_01k2dd4dg8pr8qa0jjeaad7fh7","pri_01k2dd4en2tjkb2jfvehjxxbtv","pri_01k2dd4fv4gxcccwad4v6m0mpg","pri_01k2dd4h0jes8abcbbspqkt1yh","pri_01k2dd4j5hhcewcr1khy7rgr54") },
        { 10, ("pri_01k2dd4pw5ahgp3ved7jf89j7s","pri_01k2dd4r1zka40znbs7hvz5xzb","pri_01k2dd4s7p7550gr19xkhy13hq","pri_01k2dd4tctwk1jhn8c71btw9d8","pri_01k2dd4vjftts0gr0mhknzmafq","pri_01k2dd4wrzymd53mhcrgbnsamc") },
        { 11, ("pri_01k2dd51e4bg0s843a9jwkqp9t","pri_01k2dd52m1gyt34q67z67z2ape","pri_01k2dd53rys994cfn9v38154vp","pri_01k2dd54ycpbjnnsfrhm1jychf","pri_01k2dd564eqmwyrdgtrkhzyajh","pri_01k2dd579ekhkrq2r1tesbqz5e") },
        { 12, ("pri_01k2dd5bz91948en7m8yq7ztk4","pri_01k2dd5d502yc3q0thy0389zmc","pri_01k2dd5eb1dbbvxmbpeg99fpk7","pri_01k2dd5fg59b30n8bb8shq24b6","pri_01k2dd5gnpttv4k8pc9nkjwen1","pri_01k2dd5hvhxvrmb4n9bzrskhqr") },
        { 13, ("pri_01k2dd5ph0tzva2vf8asysgbnh","pri_01k2dd5qq857nc8fqq5ww7x637","pri_01k2dd5rwwqpqsfqcdp5gqq47t","pri_01k2dd5t1vzxnwg53x5zzykfn7","pri_01k2dd5v7tckgx858fm2pdbd7p","pri_01k2dd5wcxnt7c16er8zzqnhps") },
        { 14, ("pri_01k2dd6131sktzk1rd23m2y6g9","pri_01k2dd628m0grf0qpexnmz1b3y","pri_01k2dd63eaxc7k905j4hwrk3d5","pri_01k2dd64m271b0fmtkqbk7s1dz","pri_01k2dd65tbtfvvq0meysm1hyms","pri_01k2dd66yj8nhzx36q7ezx1xm8") },
        { 15, ("pri_01k2dd6bmj7x2pynda2xt9115q","pri_01k2dd6ct5v648zp2w05e0kp55","pri_01k2dd6dzm0kjnvy8hxyrr20g0","pri_01k2dd6f53afchf00drgwdv6x2","pri_01k2dd6gbbsnaawkjycjee4csv","pri_01k2dd6hgp8rqqd5dwm3pbgyrc") },
        { 16, ("pri_01k2dd6p70vebr72h7h8e8h0wd","pri_01k2dd6qe3kaqm8cv4b92x028g","pri_01k2dd6rjg17q7jmqz8741r0f8","pri_01k2dd6sqax5vjvab8yg7371pv","pri_01k2dd6twzejvcpsrwmmkseyb8","pri_01k2dd6w2b3t61e9aq6hshp86r") },
        { 17, ("pri_01k2dd70r954adxq0fm310j287","pri_01k2dd71xq02wahg9t84vcz048","pri_01k2dd73310jzanv6mb450tmjx","pri_01k2dd748nzffchwapjk3havwt","pri_01k2dd75ea26sgqwpesb4w3n3q","pri_01k2dd76m0bj8ew8z3t7f0g2bh") },
        { 18, ("pri_01k2dd7b9tr2cc4jrxvjzm1bzv","pri_01k2dd7cfff9486ecejkek04vf","pri_01k2dd7dmqw06nd7w5k38ykqt4","pri_01k2dd7ev3dts69nn0zmq1ga9r","pri_01k2dd7g4vmq36jkrh4e27zewa","pri_01k2dd7hbtzaeb3z92ez1j77ej") },
    };

    foreach (var kv in ids)
    {
        var careerId = kv.Key;
        var (ms, mp, mb, ys, yp, yb) = kv.Value;

        // monthly
        PaddlePriceMap.Add(ms, careerId, "starter", "monthly", M);
        PaddlePriceMap.Add(mp, careerId, "pro",     "monthly", M);
        PaddlePriceMap.Add(mb, careerId, "bonus",   "monthly", M);

        // yearly
        PaddlePriceMap.Add(ys, careerId, "starter", "yearly",  Y);
        PaddlePriceMap.Add(yp, careerId, "pro",     "yearly",  Y);
        PaddlePriceMap.Add(yb, careerId, "bonus",   "yearly",  Y);
    }
}

RegisterPaddlePriceIds();
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

using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<UserContext>();
        var dp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("ratchet");

        var vaults = db.Set<CobaltUserKeyVault>(); // <— no DbSet property needed

        var toSeed = vaults
            .Where(v => string.IsNullOrEmpty(v.EncRatchetPrivKeyJson))
            .ToList();

        foreach (var vault in toSeed)
        {
            // Generate X25519 keypair
            var gen = new X25519KeyPairGenerator();
            // If your BC version has X25519KeyGenerationParameters, use it:
            // gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
            // Otherwise use generic KeyGenerationParameters (works with many versions):
            gen.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new SecureRandom(), 255));

            var kp        = gen.GenerateKeyPair();
            var privParam = (X25519PrivateKeyParameters)kp.Private;
            var pubParam  = (X25519PublicKeyParameters)kp.Public;

            var blob = System.Text.Json.JsonSerializer.Serialize(new
            {
                priv = Convert.ToBase64String(privParam.GetEncoded()),
                pub  = Convert.ToBase64String(pubParam.GetEncoded())
            });

            var protectedBlob = protector.Protect(blob);
            vault.EncRatchetPrivKeyJson = protectedBlob;
            vault.RotatedAt = DateTime.UtcNow;
        }

        if (toSeed.Count > 0)
            db.SaveChanges();
    }
    catch (Exception ex)
    {
        if (app.Environment.IsDevelopment())
        {
            Console.WriteLine($"[KeyVaultSeed] Skipped due to error: {ex.Message}");
        }
        
    }
}

// Endpoints
app.MapGet("/", () => Results.Ok("API is up"));
app.MapGet("/health", () => Results.Ok("OK"));
app.MapControllers();
app.MapHub<ChatHub>("/chathub");

app.Run();
