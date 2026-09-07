using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using System.IO;
using CostFlow.Data;
using CostFlow.Models;
using System.Text.Json;
using CostFlow.Services;

var builder = WebApplication.CreateBuilder(args);

// Bypass corrupt system fonts for ClosedXML
try
{
    string fontPath = @"C:\Windows\Fonts\arial.ttf";
    if (!System.IO.File.Exists(fontPath)) fontPath = @"C:\Windows\Fonts\tahoma.ttf";
    if (!System.IO.File.Exists(fontPath)) fontPath = @"C:\Windows\Fonts\calibri.ttf";

    if (System.IO.File.Exists(fontPath))
    {
        var fontBytes = System.IO.File.ReadAllBytes(fontPath);
        var ms = new System.IO.MemoryStream(fontBytes);
        ClosedXML.Excel.LoadOptions.DefaultGraphicEngine =
            ClosedXML.Graphics.DefaultGraphicEngine.CreateOnlyWithFonts(ms);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error configuring ClosedXML graphic engine: {ex.Message}");
}

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
// Named client for Google Apps Script (must follow redirects for doGet)
builder.Services.AddHttpClient("GoogleAppsScript", client => { client.Timeout = TimeSpan.FromSeconds(30); })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    });

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connectionString) && (connectionString.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) || connectionString.Contains("Port=")))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(connectionString));
}

var tidbConnectionString = builder.Configuration.GetConnectionString("TiDbConnection");
builder.Services.AddDbContext<TiDbContext>(options =>
    options.UseMySql(tidbConnectionString, ServerVersion.AutoDetect(tidbConnectionString)));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IMockDateStore, MockDateStore>();
builder.Services.AddScoped<IProductPriceRepository, ProductPriceRepository>();
builder.Services.AddScoped<IDateTimeProvider, DateTimeProvider>();

// ⭐ Service สำหรับ Sync ข้อมูลไป Google Sheets (แชร์ระหว่าง Controller และ Background Service)
builder.Services.AddScoped<MonthlyOrderSyncService>();
builder.Services.AddScoped<CostFlow.Services.StockMatchingService>();
builder.Services.AddHttpClient<CostFlow.Services.ISupabaseStorageService, CostFlow.Services.SupabaseStorageService>();

// ⭐ Background Service สำหรับ Keep-Alive ป้องกัน Supabase หลับ (รันอัตโนมัติทุกๆ 12 ชม.)
builder.Services.AddHostedService<CostFlow.Services.SupabaseKeepAliveBackgroundService>();

// ⭐ Background Service สำหรับ Auto-Skip อัตโนมัติทุกวัน 00:00 (เฉพาะ Production)
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<AutoSkipBackgroundService>();
}


// ASP.NET Core Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        // Password policy (ผ่อนปรนสำหรับระบบภายในองค์กร)
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 4;

        // User settings
        options.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<CustomUserClaimsPrincipalFactory>();

// Configure Password Hasher to use fewer iterations for faster login (internal system)
builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.IterationCount = 10000;
});

// Data Protection Key Persistence (ป้องกัน Session หลุดเมื่อเซิร์ฟเวอร์รีสตาร์ตหรืออัปเดตโค้ด)
var keysFolder = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
if (!Directory.Exists(keysFolder))
{
    Directory.CreateDirectory(keysFolder);
}
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysFolder))
    .SetApplicationName("CostFlow");

// Cookie Authentication Settings (มาตรฐาน Production สำหรับระบบภายใน)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.Name = ".CostFlow.Auth";
    options.ExpireTimeSpan = TimeSpan.FromHours(8); // หมดอายุ 8 ชม. (จบ 1 กะทำงาน)
    options.SlidingExpiration = true; // ต่ออายุอัตโนมัติถ้ายังใช้งานต่อเนื่อง
    options.Cookie.IsEssential = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Login";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication(); // ← ต้องมาก่อน Authorization

// Auto-inject role claims for default accounts on every request
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var name = context.User.Identity.Name;
        if (!string.IsNullOrEmpty(name))
        {
            var identity = context.User.Identity as System.Security.Claims.ClaimsIdentity;
            if (identity != null)
            {
                if (name.Equals("ADMIN01", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.User.IsInRole("Admin"))
                    {
                        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"));
                        identity.AddClaim(new System.Security.Claims.Claim("role", "Admin"));
                    }
                }
                else if (name.Equals("DEV01", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.User.IsInRole("Dev"))
                    {
                        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Dev"));
                        identity.AddClaim(new System.Security.Claims.Claim("role", "Dev"));
                    }
                    if (!context.User.IsInRole("Admin"))
                    {
                        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"));
                        identity.AddClaim(new System.Security.Claims.Claim("role", "Admin"));
                    }
                }
                else if (name.Equals("STAFF01", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.User.IsInRole("Staff"))
                    {
                        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Staff"));
                        identity.AddClaim(new System.Security.Claims.Claim("role", "Staff"));
                    }
                }
            }
        }
    }
    await next();
});

app.UseAuthorization();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.GetTypedHeaders();
        headers.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromDays(30)
        };
    }
});

app.MapStaticAssets();

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapHub<CostFlow.Hubs.DashboardHub>("/dashboardHub");

// Auto-migrate/create Database and seed initial data on startup
await DatabaseInitializer.InitializeAsync(app.Services, app.Environment, app.Configuration);

app.Run();

// คลาสตัวช่วยชั่วคราวสำหรับอ่าน JSON ภาษาไทย
public class TempProductJsonItem
{
    public string รหัสสินค้า { get; set; } = string.Empty;
    public string ชื่อสินค้า { get; set; } = string.Empty;
    public string หน่วย { get; set; } = string.Empty;
    public double จำนวนรวม { get; set; }
    public double มูลค่ารวม { get; set; }
    public double ราคาต่อชิ้น { get; set; }
    public List<string> แหล่งข้อมูล { get; set; } = new();
}
