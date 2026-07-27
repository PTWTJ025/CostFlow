using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
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
builder.Services.AddHttpClient();
// Named client for Google Apps Script (must follow redirects for doGet)
builder.Services.AddHttpClient("GoogleAppsScript", client => { client.Timeout = TimeSpan.FromSeconds(30); })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    });

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

builder.Services.AddScoped<IProductPriceRepository, ProductPriceRepository>();

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

// Cookie Authentication Settings
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8); // หมดอายุ 8 ชม.
    options.SlidingExpiration = true; // ต่ออายุอัตโนมัติถ้ายังใช้งานอยู่
    options.Cookie.IsEssential = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Login";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication(); // ← ต้องมาก่อน Authorization
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

// Auto-migrate/create Database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // 1. Recreate all tables if DB doesn't exist (Identity + custom tables)
    db.Database.EnsureCreated();

    // 2. Seed Product Prices only if the table is empty
    if (!db.ProductPrices.Any())
    {
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var jsonPath = Path.Combine(env.WebRootPath, "data", "price_per_unit.json");

        if (File.Exists(jsonPath))
        {
            try
            {
                var jsonString = File.ReadAllText(jsonPath);
                var rawItems = JsonSerializer.Deserialize<List<TempProductJsonItem>>(jsonString);

                if (rawItems != null)
                {
                    var products = rawItems.Select(x => new ProductPrice
                    {
                        ProductCode = x.รหัสสินค้า,
                        ProductName = x.ชื่อสินค้า,
                        Unit = x.หน่วย,
                        TotalQty = x.จำนวนรวม,
                        TotalValue = x.มูลค่ารวม,
                        PricePerUnit = x.ราคาต่อชิ้น,
                        Sources = string.Join(", ", x.แหล่งข้อมูล)
                    }).ToList();

                    db.ProductPrices.AddRange(products);
                    db.SaveChanges();
                    Console.WriteLine($"Successfully seeded {products.Count} product prices into the database.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error seeding product prices: {ex.Message}");
            }
        }
    }

    // 3. Seed Roles & Default Users via Identity
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    // Create roles
    string[] roles = { "Admin", "Staff" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }

    // อ่านรหัสผ่านจาก Environment Variable หรือ Configuration (ใน Development อนุญาตให้ใช้รหัสผ่านเริ่มต้นเพื่อความสะดวก)
    string? adminPassword = app.Configuration["Seed:AdminPassword"]
                            ?? Environment.GetEnvironmentVariable("ADMIN_PASSWORD")
                            ?? (app.Environment.IsDevelopment() ? "admin1234" : null);

    string? staffPassword = app.Configuration["Seed:StaffPassword"]
                            ?? Environment.GetEnvironmentVariable("STAFF_PASSWORD")
                            ?? (app.Environment.IsDevelopment() ? "123456" : null);

    // Seed ADMIN01
    var adminUser = await userManager.FindByNameAsync("ADMIN01");
    if (adminUser == null)
    {
        if (string.IsNullOrEmpty(adminPassword))
        {
            Console.WriteLine("WARNING: Skipping ADMIN01 creation in Production because ADMIN_PASSWORD is not set.");
        }
        else
        {
            var admin = new ApplicationUser
            {
                UserName = "ADMIN01",
                EmployeeCode = "ADMIN01",
                FullName = "แมวกวนๆ",
                ProfilePictureUrl = "https://cdn.readawrite.com/articles/11729/11728659/thumbnail/large.gif?1",
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            var result = await userManager.CreateAsync(admin, adminPassword);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, "Admin");
                Console.WriteLine("Seeded user ADMIN01 with role Admin.");
            }
            else
            {
                Console.WriteLine(
                    $"Failed to seed ADMIN01: {string.Join(", ", result.Errors.Select(e => e.Description))}");
            }
        }
    }
    else
    {
        adminUser.FullName = "แมวกวนๆ";
        adminUser.ProfilePictureUrl = "https://cdn.readawrite.com/articles/11729/11728659/thumbnail/large.gif?1";
        await userManager.UpdateAsync(adminUser);
    }

    // Seed STAFF01
    var staffUser = await userManager.FindByNameAsync("STAFF01");
    if (staffUser == null)
    {
        if (string.IsNullOrEmpty(staffPassword))
        {
            Console.WriteLine("WARNING: Skipping STAFF01 creation in Production because STAFF_PASSWORD is not set.");
        }
        else
        {
            var staff = new ApplicationUser
            {
                UserName = "STAFF01",
                EmployeeCode = "STAFF01",
                FullName = "เจ้าหน้าที่ฝ่ายช่าง",
                ProfilePictureUrl =
                    "https://png.pngtree.com/png-clipart/20240304/original/pngtree-repairman-worker-cat-sticker-png-image_14504417.png",
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            var result = await userManager.CreateAsync(staff, staffPassword);
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(staff, "Staff");
                Console.WriteLine("Seeded user STAFF01 with role Staff.");
            }
            else
            {
                Console.WriteLine(
                    $"Failed to seed STAFF01: {string.Join(", ", result.Errors.Select(e => e.Description))}");
            }
        }
    }
    else
    {
        staffUser.FullName = "เจ้าหน้าที่ฝ่ายช่าง";
        staffUser.ProfilePictureUrl =
            "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcRkt942qIdGSgY_RotRR9_HhY3bveTRjSgZZYygIyA-3JzTkNbA9PR1CbXB&s=10";
        await userManager.UpdateAsync(staffUser);
    }
}

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
