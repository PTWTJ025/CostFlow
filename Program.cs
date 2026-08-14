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

// ⭐ Background Service สำหรับ Auto-Skip อัตโนมัติทุกวัน 00:00 (เฉพาะ Production ไม่รันตอนทดสอบ Dev)
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

app.MapHub<CostFlow.Hubs.DashboardHub>("/dashboardHub");

// Auto-migrate/create Database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var tiDb = scope.ServiceProvider.GetRequiredService<TiDbContext>();

    // 1. Recreate all tables if DB doesn't exist (Identity + custom tables)
    db.Database.EnsureCreated();
    tiDb.Database.EnsureCreated();

    try
    {
        tiDb.Database.ExecuteSqlRaw("ALTER TABLE SavedOrderItems ADD COLUMN IsReceived BOOLEAN NOT NULL DEFAULT 0;");
    }
    catch
    {
        /* Ignore if column already exists */
    }

    try
    {
        tiDb.Database.ExecuteSqlRaw("ALTER TABLE SavedOrderItems ADD COLUMN ReceiveDate DATETIME NULL;");
    }
    catch
    {
        /* Ignore if column already exists */
    }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Reports ADD COLUMN CreatedBy TEXT NULL;");
    }
    catch
    {
        /* Ignore if column already exists */
    }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Reports ADD COLUMN CreatedByUserId TEXT NULL;");
    }
    catch
    {
        /* Ignore if column already exists */
    }

    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE WeeklyPlans ADD COLUMN UploadedBy TEXT NULL;");
    }
    catch
    {
        /* Ignore if column already exists */
    }

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

    // 2.5 Seed Sample Staff Orders into TiDbContext if empty
    if (!tiDb.SavedOrderBatches.Any())
    {
        try
        {
            var now = DateTime.Now;
            var sampleBatches = new List<CostFlow.Models.TiDb.SavedOrderBatch>
            {
                new CostFlow.Models.TiDb.SavedOrderBatch
                {
                    BatchName = "BATCH-20250811-001",
                    CreatedAt = now.AddDays(-10),
                    TotalItems = 2,
                    TotalAmount = 3450.00m,
                    Items = new List<CostFlow.Models.TiDb.SavedOrderItem>
                    {
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "BEARING-6204-ZZ", ProductName = "ลูกปืนตลับ SKF 6204-ZZ", Unit = "ตัว", UnitPrice = 185.00m, Quantity = 10, Remarks = "สำหรับบำรุงรักษาเครื่องกลึง T-101", IsReceived = true, ReceiveDate = now.AddDays(-8) },
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "BELT-B-65", ProductName = "สายพานพัดลม B-65 Mitsuboshi", Unit = "เส้น", UnitPrice = 320.00m, Quantity = 5, Remarks = "เปลี่ยนตามรอบซ่อมบำรุง", IsReceived = true, ReceiveDate = now.AddDays(-7) }
                    }
                },
                new CostFlow.Models.TiDb.SavedOrderBatch
                {
                    BatchName = "BATCH-20250811-002",
                    CreatedAt = now.AddDays(-7),
                    TotalItems = 2,
                    TotalAmount = 6100.00m,
                    Items = new List<CostFlow.Models.TiDb.SavedOrderItem>
                    {
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "VALVE-SOL-24V", ProductName = "โซลินอยด์วาล์ว 24VDC SMC", Unit = "ตัว", UnitPrice = 2450.00m, Quantity = 2, Remarks = "งานซ่อมด่วนไลน์ผลิต A", IsReceived = false },
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "PU-TUBE-8MM", ProductName = "ท่อนิวเมติก PU 8mm (ม้วน 100m)", Unit = "ม้วน", UnitPrice = 1200.00m, Quantity = 1, Remarks = "เดินท่อลมใหม่ไลน์ A", IsReceived = true, ReceiveDate = now.AddDays(-5) }
                    }
                },
                new CostFlow.Models.TiDb.SavedOrderBatch
                {
                    BatchName = "BATCH-20250811-003",
                    CreatedAt = now.AddDays(-5),
                    TotalItems = 2,
                    TotalAmount = 17900.00m,
                    Items = new List<CostFlow.Models.TiDb.SavedOrderItem>
                    {
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "GREASE-HIGH-TEMP", ProductName = "จารบีทนความร้อน SKF LGMT 3/1", Unit = "กระป๋อง", UnitPrice = 850.00m, Quantity = 4, Remarks = "สั่งซื้อสำรองคลังช่างประจำเดือน", IsReceived = false },
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "OIL-HYD-68", ProductName = "น้ำมันไฮดรอลิก PTT ISO VG 68 (ถัง 200L)", Unit = "ถัง", UnitPrice = 14500.00m, Quantity = 1, Remarks = "เปลี่ยนถ่ายประจำปีเครื่องปั๊ม", IsReceived = false }
                    }
                },
                new CostFlow.Models.TiDb.SavedOrderBatch
                {
                    BatchName = "BATCH-20250811-004",
                    CreatedAt = now.AddDays(-3),
                    TotalItems = 2,
                    TotalAmount = 15400.00m,
                    Items = new List<CostFlow.Models.TiDb.SavedOrderItem>
                    {
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "MOTOR-3HP-3P", ProductName = "มอเตอร์ไฟฟ้า Mitsubishi 3HP 380V", Unit = "เครื่อง", UnitPrice = 8900.00m, Quantity = 1, Remarks = "สำหรับประกอบสายพานลำเลียงใหม่", IsReceived = true, ReceiveDate = now.AddDays(-1) },
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "GEAR-REDUCER-1-30", ProductName = "เกียร์ทดรอบ อัตราส่วน 1:30", Unit = "ตัว", UnitPrice = 6500.00m, Quantity = 1, Remarks = "ใช้คู่กับมอเตอร์ 3HP", IsReceived = true, ReceiveDate = now.AddDays(-1) }
                    }
                },
                new CostFlow.Models.TiDb.SavedOrderBatch
                {
                    BatchName = "BATCH-20250811-005",
                    CreatedAt = now.AddDays(-1),
                    TotalItems = 2,
                    TotalAmount = 3150.00m,
                    Items = new List<CostFlow.Models.TiDb.SavedOrderItem>
                    {
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "BOLT-M12-50-SS", ProductName = "น็อตสแตนเลส M12x50mm (กล่อง 100 ตัว)", Unit = "กล่อง", UnitPrice = 750.00m, Quantity = 3, Remarks = "ประกอบยึดแท่นเครื่องจักร T-99", IsReceived = false },
                        new CostFlow.Models.TiDb.SavedOrderItem { ProductCode = "WASHER-M12-SS", ProductName = "แหวนสปริงสแตนเลส M12 (กล่อง 500 ตัว)", Unit = "กล่อง", UnitPrice = 450.00m, Quantity = 2, Remarks = "งานประกอบเครื่องจักร T-99", IsReceived = false }
                    }
                }
            };

            tiDb.SavedOrderBatches.AddRange(sampleBatches);
            tiDb.SaveChanges();
            Console.WriteLine("Successfully seeded 5 sample staff order batches into TiDbContext.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error seeding staff orders: {ex.Message}");
        }
    }

    // 3. Seed Roles & Default Users via Identity
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    // Create roles
    string[] roles = { "Admin", "Staff", "Dev" };
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

    // Seed DEV01 (ทีมงาน Dev Test - Dev role สำหรับทดสอบระบบ)
    string? devPassword = app.Configuration["Seed:DevPassword"]
                          ?? Environment.GetEnvironmentVariable("DEV_PASSWORD")
                          ?? (app.Environment.IsDevelopment() ? "dev1234" : "123456");

    var devUser = await userManager.FindByNameAsync("DEV01");
    if (devUser == null)
    {
        var dev = new ApplicationUser
        {
            UserName = "DEV01",
            EmployeeCode = "DEV01",
            FullName = "ทีมงาน Dev Test",
            ProfilePictureUrl = "https://img-9gag-fun.9cache.com/photo/ap93yAE_460s.jpg",
            IsActive = true,
            CreatedAt = DateTime.Now
        };
        var result = await userManager.CreateAsync(dev, devPassword);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(dev, "Dev");
            Console.WriteLine("Seeded user DEV01 with role Dev.");
        }
        else
        {
            Console.WriteLine($"Failed to seed DEV01: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }
    }
    else
    {
        devUser.FullName = "ทีมงาน Dev Test";
        await userManager.UpdateAsync(devUser);
        // ย้าย role จาก Admin → Dev ถ้ายังเป็น Admin อยู่
        var devRoles = await userManager.GetRolesAsync(devUser);
        if (devRoles.Contains("Admin") && !devRoles.Contains("Dev"))
        {
            await userManager.RemoveFromRoleAsync(devUser, "Admin");
            await userManager.AddToRoleAsync(devUser, "Dev");
            Console.WriteLine("Migrated DEV01 role: Admin → Dev.");
        }
    }

    // ทำความสะอาด MonthYear ที่มีความยาวเกิน 7 ตัวอักษร (เช่น 2025-122 -> 2025-12) และลบ duplicate actions
    try
    {
        var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await appDb.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET MonthYear = SUBSTR(MonthYear, 1, 7) WHERE LENGTH(MonthYear) > 7;");
        await appDb.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET DeferredFromMonth = SUBSTR(DeferredFromMonth, 1, 7) WHERE DeferredFromMonth IS NOT NULL AND LENGTH(DeferredFromMonth) > 7;");
        
        // ลบ duplicate actions ถ้ามี (เก็บตัวล่าสุดไว้)
        await appDb.Database.ExecuteSqlRawAsync(@"
            DELETE FROM MonthlyOrderActions
            WHERE Id NOT IN (
                SELECT Id FROM (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY OrderTrackingMasterId, MonthYear 
                               ORDER BY CreatedAt DESC, Id DESC
                           ) as rn
                    FROM MonthlyOrderActions
                )
                WHERE rn = 1
            );");
        Console.WriteLine("Verified and sanitized MonthlyOrderActions MonthYear formats.");
    }
    catch (Exception dbEx)
    {
        Console.WriteLine($"DB sanitize warning: {dbEx.Message}");
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
