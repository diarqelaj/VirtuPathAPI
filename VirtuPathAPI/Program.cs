// Program.cs  –  VirtuPathAPI
//------------------------------------------------------------
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using VirtuPathAPI.Models;
using VirtuPathAPI.Hubs; // 👈 Add this for ChatHub
using Microsoft.AspNetCore.SignalR;
using VirtuPathAPI.Data;

var builder = WebApplication.CreateBuilder(args);

//------------------------------------------------------------
// 1)  DATABASE CONTEXTS
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
builder.Services.AddDbContext<ChatContext>(opt => opt.UseSqlServer(cs)); // 👈 Add this for chat messages

builder.Services.AddDbContext<ChatContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

//------------------------------------------------------------
// 2)  CORS Policies
//------------------------------------------------------------
builder.Services.AddCors(options =>
{
    // ✅ Allow only the deployed frontend
    options.AddPolicy("AllowFrontend", p =>
    {
        p.WithOrigins("https://virtu-path-ai.vercel.app")
         .AllowCredentials()
         .AllowAnyHeader()
         .AllowAnyMethod();
    });

    // ✅ Allow Swagger UI in development
    options.AddPolicy("AllowSwagger", p =>
    {
        p.WithOrigins("https://localhost:7072", "https://localhost:3000", "http://localhost:3000")

         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials();
    });
});

//------------------------------------------------------------
// 3)  SESSION + COOKIE POLICY
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
// 4)  MVC / Swagger / SignalR
//------------------------------------------------------------
builder.Services.AddSignalR(); // 👈 Add SignalR support

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
// 5)  BUILD
//------------------------------------------------------------
var app = builder.Build();

//------------------------------------------------------------
// 6)  PIPELINE
//------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ✅ Always allow Vercel frontend
app.UseCors("AllowFrontend");

app.UseHttpsRedirection();

app.UseCookiePolicy();
app.UseSession();

app.UseStaticFiles();

//------------------------------------------------------------
// 7)  “REMEMBER-ME” RE-HYDRATE MIDDLEWARE
//------------------------------------------------------------
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

//------------------------------------------------------------
// 8)  MAP SIGNALR HUB
//------------------------------------------------------------
app.MapHub<ChatHub>("/chathub"); // 👈 SignalR ChatHub endpoint

app.Run();
