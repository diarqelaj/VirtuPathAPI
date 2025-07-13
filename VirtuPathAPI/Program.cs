// Program.cs – VirtuPathAPI
//------------------------------------------------------------
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using VirtuPathAPI.Models;
using VirtuPathAPI.Hubs;
using VirtuPathAPI.Data;
using Microsoft.AspNetCore.HttpOverrides;

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
builder.Services.AddDbContext<CommunityPostContext>(opt => opt.UseSqlServer(cs));


builder.Services.AddDbContext<ChatContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("VirtuPathDB")));

// 👇 ChatContext uses different connection string

//------------------------------------------------------------
// 2) SIGNALR
//------------------------------------------------------------
builder.Services.AddSignalR();

//------------------------------------------------------------
// 3) CORS Policies
//------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", p =>
    {
        p.WithOrigins("https://virtu-path-ai.vercel.app")
         .AllowCredentials()
         .AllowAnyHeader()
         .AllowAnyMethod();
    });

    options.AddPolicy("AllowSwagger", p =>
    {
        p.WithOrigins("https://localhost:7072", "https://localhost:3000", "https://localhost:3000")
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials();
    });
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

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
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

app.MapControllers();
app.MapHub<ChatHub>("/chathub"); // ✅ SignalR endpoint

app.Run();
