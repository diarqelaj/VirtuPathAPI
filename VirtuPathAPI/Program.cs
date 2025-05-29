// Program.cs – VirtuPathAPI
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

// Your ChatContext
builder.Services.AddDbContext<ChatContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("VirtuPathDB")));

//------------------------------------------------------------
// 2) SIGNALR
//------------------------------------------------------------


builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.AddHttpContextAccessor();
builder.Services
  .AddSignalR()
  .AddJsonProtocol(options => {
    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.DictionaryKeyPolicy  = JsonNamingPolicy.CamelCase;
  });

// allow us to read the session inside SignalR


// hook SignalR’s UserIdentifier to our session “UserID”
builder.Services.AddSingleton<IUserIdProvider, SessionUserIdProvider>();
// Set your Cloudinary credentials
//=================================

DotEnv.Load(options: new DotEnvOptions(probeForEnv: true));
var rawUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL")?.Trim();
var cloudinary = new Cloudinary(rawUrl);

cloudinary.Api.Secure = true;
builder.Services.AddSingleton(cloudinary);
//------------------------------------------------------------
// 3) CORS Policies
//------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p =>
    {
        p.WithOrigins(
            "https://virtu-path-ai.vercel.app",
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
builder.Services
       .AddControllers()
       .AddJsonOptions(opts => {
         opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
       });
//------------------------------------------------------------
// 4) SESSION + COOKIE POLICY
//------------------------------------------------------------
builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(opt =>
{
    opt.Cookie.Name = ".VirtuPath.Session";
    opt.Cookie.HttpOnly = true;
    opt.Cookie.IsEssential = true;
    opt.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opt.Cookie.SameSite = SameSiteMode.None;
    opt.IdleTimeout = TimeSpan.FromMinutes(360);
});

//------------------------------------------------------------
// 5) MVC / JSON / SWAGGER
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
// 6) BUILD
//------------------------------------------------------------
var app = builder.Build();

//------------------------------------------------------------
// 7) PIPELINE
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

app.UseAuthorization();

// Map your API controllers…
app.MapControllers();

// …and wire up SignalR here:
app.MapHub<ChatHub>("/chathub"); // ✅ SignalR endpoint

app.Run();
