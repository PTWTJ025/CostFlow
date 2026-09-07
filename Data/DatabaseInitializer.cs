using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CostFlow.Models;
using TiDbBatch = CostFlow.Models.TiDb.SavedOrderBatch;
using TiDbItem = CostFlow.Models.TiDb.SavedOrderItem;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CostFlow.Data
{
    public static class DatabaseInitializer
    {
        public static async Task InitializeAsync(
            IServiceProvider services,
            IWebHostEnvironment env,
            IConfiguration configuration)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tiDb = scope.ServiceProvider.GetRequiredService<TiDbContext>();

            // 1. Ensure basic creation (for SQLite or fresh DBs)
            try
            {
                db.Database.EnsureCreated();
                tiDb.Database.EnsureCreated();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Init] EnsureCreated warning: {ex.Message}");
            }

            // 2. Explicitly create all required tables if missing (fixes MySQL/TiDB EnsureCreated partial schema issue)
            await EnsureMySqlTablesExistAsync(db, tiDb);

            // 3. Apply necessary schema column updates
            await ApplySchemaMigrationsAsync(db, tiDb);

            // 4. Seed Product Prices from JSON if table is empty
            await SeedProductPricesAsync(db, env);

            // 5. Seed Sample Staff Orders into TiDbContext if empty
            await SeedSampleStaffOrdersAsync(tiDb);

            // 6. Seed Roles & Default Users via Identity
            await SeedIdentityUsersAndRolesAsync(scope.ServiceProvider, env, configuration);

            // 7. Sanitize MonthlyOrderActions data
            await SanitizeMonthlyOrderActionsAsync(db);

            // 8. Seed Stock Items from Excel if empty
            await SeedStockItemsFromExcelAsync(db, env);

            // 9. Fix existing Category Codes
            await FixCategoryCodesAsync(db);
        }

        private static async Task FixCategoryCodesAsync(AppDbContext db)
        {
            var needsFix = await db.StockItems.AnyAsync(s => s.Category == "b7392108" || s.Category == "b453465a");
            if (needsFix)
            {
                Console.WriteLine("[DB Init] Fixing Category Codes to Thai names...");
                var items = await db.StockItems.Where(s => s.Category == "b7392108" || s.Category == "b453465a").ToListAsync();
                foreach (var item in items)
                {
                    if (item.Category == "b7392108") item.Category = "อะไหล่";
                    if (item.Category == "b453465a") item.Category = "อะไหล่เวียน";
                }
                await db.SaveChangesAsync();
            }
        }

        public static async Task EnsureMySqlTablesExistAsync(AppDbContext db, TiDbContext tiDb)
        {
            var isMySql = db.Database.IsMySql();
            if (!isMySql) return;

            Console.WriteLine("[DB Init] Verifying MySQL/TiDB database schema tables...");

            var createTableSqlStatements = new List<string>
            {
                // Identity Tables
                @"CREATE TABLE IF NOT EXISTS `AspNetRoles` (
                    `Id` varchar(255) NOT NULL,
                    `Name` varchar(256) DEFAULT NULL,
                    `NormalizedName` varchar(256) DEFAULT NULL,
                    `ConcurrencyStamp` longtext DEFAULT NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE KEY `RoleNameIndex` (`NormalizedName`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `AspNetUsers` (
                    `Id` varchar(255) NOT NULL,
                    `EmployeeCode` varchar(255) NOT NULL DEFAULT '',
                    `FullName` longtext NOT NULL,
                    `ProfilePictureUrl` longtext DEFAULT NULL,
                    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
                    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `UserName` varchar(256) DEFAULT NULL,
                    `NormalizedUserName` varchar(256) DEFAULT NULL,
                    `Email` varchar(256) DEFAULT NULL,
                    `NormalizedEmail` varchar(256) DEFAULT NULL,
                    `EmailConfirmed` tinyint(1) NOT NULL DEFAULT 0,
                    `PasswordHash` longtext DEFAULT NULL,
                    `SecurityStamp` longtext DEFAULT NULL,
                    `ConcurrencyStamp` longtext DEFAULT NULL,
                    `PhoneNumber` longtext DEFAULT NULL,
                    `PhoneNumberConfirmed` tinyint(1) NOT NULL DEFAULT 0,
                    `TwoFactorEnabled` tinyint(1) NOT NULL DEFAULT 0,
                    `LockoutEnd` datetime(6) DEFAULT NULL,
                    `LockoutEnabled` tinyint(1) NOT NULL DEFAULT 0,
                    `AccessFailedCount` int NOT NULL DEFAULT 0,
                    PRIMARY KEY (`Id`),
                    UNIQUE KEY `UserNameIndex` (`NormalizedUserName`),
                    UNIQUE KEY `IX_AspNetUsers_EmployeeCode` (`EmployeeCode`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `AspNetRoleClaims` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `RoleId` varchar(255) NOT NULL,
                    `ClaimType` longtext DEFAULT NULL,
                    `ClaimValue` longtext DEFAULT NULL,
                    PRIMARY KEY (`Id`),
                    KEY `IX_AspNetRoleClaims_RoleId` (`RoleId`),
                    CONSTRAINT `FK_AspNetRoleClaims_AspNetRoles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `AspNetRoles` (`Id`) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `AspNetUserClaims` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `UserId` varchar(255) NOT NULL,
                    `ClaimType` longtext DEFAULT NULL,
                    `ClaimValue` longtext DEFAULT NULL,
                    PRIMARY KEY (`Id`),
                    KEY `IX_AspNetUserClaims_UserId` (`UserId`),
                    CONSTRAINT `FK_AspNetUserClaims_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `AspNetUserLogins` (
                    `LoginProvider` varchar(255) NOT NULL,
                    `ProviderKey` varchar(255) NOT NULL,
                    `ProviderDisplayName` longtext DEFAULT NULL,
                    `UserId` varchar(255) NOT NULL,
                    PRIMARY KEY (`LoginProvider`, `ProviderKey`),
                    KEY `IX_AspNetUserLogins_UserId` (`UserId`),
                    CONSTRAINT `FK_AspNetUserLogins_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `AspNetUserRoles` (
                    `UserId` varchar(255) NOT NULL,
                    `RoleId` varchar(255) NOT NULL,
                    PRIMARY KEY (`UserId`, `RoleId`),
                    KEY `IX_AspNetUserRoles_RoleId` (`RoleId`),
                    CONSTRAINT `FK_AspNetUserRoles_AspNetRoles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `AspNetRoles` (`Id`) ON DELETE CASCADE,
                    CONSTRAINT `FK_AspNetUserRoles_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `AspNetUserTokens` (
                    `UserId` varchar(255) NOT NULL,
                    `LoginProvider` varchar(255) NOT NULL,
                    `Name` varchar(255) NOT NULL,
                    `Value` longtext DEFAULT NULL,
                    PRIMARY KEY (`UserId`, `LoginProvider`, `Name`),
                    CONSTRAINT `FK_AspNetUserTokens_AspNetUsers_UserId` FOREIGN KEY (`UserId`) REFERENCES `AspNetUsers` (`Id`) ON DELETE CASCADE
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                // AppDbContext Business Tables
                @"CREATE TABLE IF NOT EXISTS `ProductPrices` (
                    `ProductCode` varchar(255) NOT NULL,
                    `ProductName` longtext NOT NULL,
                    `Unit` longtext NOT NULL,
                    `TotalQty` double NOT NULL DEFAULT 0,
                    `TotalValue` double NOT NULL DEFAULT 0,
                    `PricePerUnit` double NOT NULL DEFAULT 0,
                    `Sources` longtext NOT NULL,
                    PRIMARY KEY (`ProductCode`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `Reports` (
                    `Id` char(36) NOT NULL,
                    `ReportName` varchar(255) NOT NULL,
                    `OriginalFileName` longtext NOT NULL,
                    `TotalPOs` int NOT NULL DEFAULT 0,
                    `MatchedPOs` int NOT NULL DEFAULT 0,
                    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `CreatedBy` longtext DEFAULT NULL,
                    `CreatedByUserId` longtext DEFAULT NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE KEY `IX_Reports_ReportName` (`ReportName`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `OrderTrackingMasters` (
                    `Id` char(36) NOT NULL,
                    `ReportId` char(36) NOT NULL,
                    `PoNumber` longtext NOT NULL,
                    `RequestDate` longtext DEFAULT NULL,
                    `ApprovedDate` longtext DEFAULT NULL,
                    `Urgency` longtext DEFAULT NULL,
                    `Amount` longtext DEFAULT NULL,
                    `Remarks` longtext DEFAULT NULL,
                    `RemarksQuantity` longtext DEFAULT NULL,
                    `Status` longtext NOT NULL,
                    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    PRIMARY KEY (`Id`),
                    KEY `IX_OrderTrackingMasters_ReportId` (`ReportId`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `WeeklyPlans` (
                    `Id` char(36) NOT NULL,
                    `ReportId` char(36) NOT NULL,
                    `FileName` longtext NOT NULL,
                    `SheetName` longtext NOT NULL,
                    `TotalRecords` int NOT NULL DEFAULT 0,
                    `MatchedCount` int NOT NULL DEFAULT 0,
                    `UploadedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `UploadedBy` longtext DEFAULT NULL,
                    PRIMARY KEY (`Id`),
                    KEY `IX_WeeklyPlans_ReportId` (`ReportId`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `WeeklyPlanDetails` (
                    `Id` char(36) NOT NULL,
                    `WeeklyPlanId` char(36) NOT NULL,
                    `PoNumberInFile` longtext DEFAULT NULL,
                    `Department` longtext DEFAULT NULL,
                    `OrderName` longtext DEFAULT NULL,
                    `OrderStatus` longtext DEFAULT NULL,
                    `DeliveryTarget` longtext DEFAULT NULL,
                    `Price` longtext DEFAULT NULL,
                    `RowIndex` int NOT NULL DEFAULT 0,
                    `IsMatched` tinyint(1) NOT NULL DEFAULT 0,
                    `MatchedOrderId` char(36) DEFAULT NULL,
                    PRIMARY KEY (`Id`),
                    KEY `IX_WeeklyPlanDetails_MatchedOrderId` (`MatchedOrderId`),
                    KEY `IX_WeeklyPlanDetails_WeeklyPlanId` (`WeeklyPlanId`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `MonthlyOrderActions` (
                    `Id` char(36) NOT NULL,
                    `OrderTrackingMasterId` char(36) NOT NULL,
                    `MonthYear` varchar(50) NOT NULL,
                    `Action` varchar(50) NOT NULL DEFAULT 'Pending',
                    `ActionPrice` decimal(18,2) NOT NULL DEFAULT 0.00,
                    `DeferredFromMonth` varchar(50) DEFAULT NULL,
                    `IsForcedPayment` tinyint(1) NOT NULL DEFAULT 0,
                    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    PRIMARY KEY (`Id`),
                    KEY `IX_MonthlyOrderActions_OrderTrackingMasterId` (`OrderTrackingMasterId`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                // Stock System Tables
                @"CREATE TABLE IF NOT EXISTS `StockItems` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `ProductCode` varchar(100) NOT NULL,
                    `ProductName` varchar(255) NOT NULL,
                    `Category` varchar(100) DEFAULT NULL,
                    `FilePath` varchar(500) DEFAULT NULL,
                    `InitialStock` decimal(18,4) NOT NULL DEFAULT 0.0000,
                    `Quantity` decimal(18,4) NOT NULL DEFAULT 0.0000,
                    `MinStock` decimal(18,4) NOT NULL DEFAULT 0.0000,
                    `MaxStock` decimal(18,4) NOT NULL DEFAULT 0.0000,
                    `StockStatus` varchar(50) DEFAULT NULL,
                    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    PRIMARY KEY (`Id`),
                    UNIQUE KEY `IX_StockItems_ProductCode` (`ProductCode`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `ItemMappings` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `OrderName` varchar(500) NOT NULL,
                    `StockItemCode` varchar(100) NOT NULL,
                    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    PRIMARY KEY (`Id`),
                    UNIQUE KEY `IX_ItemMappings_OrderName` (`OrderName`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                @"CREATE TABLE IF NOT EXISTS `StockLogs` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `StockItemCode` varchar(100) NOT NULL,
                    `Action` varchar(50) NOT NULL,
                    `QuantityChanged` decimal(18,4) NOT NULL DEFAULT 0.0000,
                    `ReferenceId` varchar(255) DEFAULT NULL,
                    `Remarks` varchar(500) DEFAULT NULL,
                    `Timestamp` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    `User` varchar(255) DEFAULT NULL,
                    PRIMARY KEY (`Id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;"
            };

            foreach (var sql in createTableSqlStatements)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(sql);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB Init AppDb Table Warning] {ex.Message}");
                }
            }

            // TiDbContext Tables
            if (tiDb.Database.IsMySql())
            {
                var tiDbSqlStatements = new List<string>
                {
                    @"CREATE TABLE IF NOT EXISTS `SavedOrderBatches` (
                        `Id` int NOT NULL AUTO_INCREMENT,
                        `BatchName` varchar(255) NOT NULL,
                        `CreatedAt` datetime(6) NOT NULL,
                        `TotalItems` int NOT NULL,
                        `TotalAmount` decimal(65,30) NOT NULL,
                        PRIMARY KEY (`Id`),
                        UNIQUE KEY `IX_SavedOrderBatches_BatchName` (`BatchName`)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;",

                    @"CREATE TABLE IF NOT EXISTS `SavedOrderItems` (
                        `Id` int NOT NULL AUTO_INCREMENT,
                        `BatchId` int NOT NULL,
                        `ProductCode` varchar(100) NOT NULL DEFAULT '',
                        `ProductName` varchar(255) NOT NULL DEFAULT '',
                        `Unit` varchar(50) NOT NULL DEFAULT '',
                        `UnitPrice` decimal(65,30) NOT NULL,
                        `Quantity` decimal(65,30) NOT NULL,
                        `Remarks` longtext NOT NULL,
                        `IsReceived` tinyint(1) NOT NULL DEFAULT 0,
                        `ReceiveDate` datetime(6) DEFAULT NULL,
                        PRIMARY KEY (`Id`),
                        KEY `IX_SavedOrderItems_BatchId` (`BatchId`),
                        CONSTRAINT `FK_SavedOrderItems_SavedOrderBatches_BatchId` FOREIGN KEY (`BatchId`) REFERENCES `SavedOrderBatches` (`Id`) ON DELETE CASCADE
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;"
                };

                foreach (var sql in tiDbSqlStatements)
                {
                    try
                    {
                        await tiDb.Database.ExecuteSqlRawAsync(sql);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DB Init TiDb Table Warning] {ex.Message}");
                    }
                }
            }

            Console.WriteLine("[DB Init] MySQL/TiDB database schema verified successfully.");
        }

        private static async Task ApplySchemaMigrationsAsync(AppDbContext db, TiDbContext tiDb)
        {
            try
            {
                await tiDb.Database.ExecuteSqlRawAsync("ALTER TABLE SavedOrderItems ADD COLUMN IsReceived BOOLEAN NOT NULL DEFAULT 0;");
            }
            catch { /* Column already exists */ }

            try
            {
                await tiDb.Database.ExecuteSqlRawAsync("ALTER TABLE SavedOrderItems ADD COLUMN ReceiveDate DATETIME NULL;");
            }
            catch { /* Column already exists */ }

            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Reports ADD COLUMN CreatedBy TEXT NULL;");
            }
            catch { /* Column already exists */ }

            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Reports ADD COLUMN CreatedByUserId TEXT NULL;");
            }
            catch { /* Column already exists */ }

            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE WeeklyPlans ADD COLUMN UploadedBy TEXT NULL;");
            }
            catch { /* Column already exists */ }

            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE StockItems ADD COLUMN FilePath varchar(500) NULL;");
            }
            catch { /* Column already exists */ }

            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE StockItems ADD COLUMN InitialStock decimal(18,4) NOT NULL DEFAULT 0.0000;");
            }
            catch { /* Column already exists */ }
        }

        private static async Task SeedProductPricesAsync(AppDbContext db, IWebHostEnvironment env)
        {
            if (await db.ProductPrices.AnyAsync()) return;

            var jsonPath = Path.Combine(env.WebRootPath, "data", "price_per_unit.json");
            if (!File.Exists(jsonPath)) return;

            try
            {
                var jsonString = await File.ReadAllTextAsync(jsonPath);
                var rawItems = JsonSerializer.Deserialize<List<TempProductJsonItem>>(jsonString);

                if (rawItems != null && rawItems.Count > 0)
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

                    await db.ProductPrices.AddRangeAsync(products);
                    await db.SaveChangesAsync();
                    Console.WriteLine($"[DB Init] Successfully seeded {products.Count} product prices into the database.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Init] Error seeding product prices: {ex.Message}");
            }
        }

        private static async Task SeedSampleStaffOrdersAsync(TiDbContext tiDb)
        {
            if (await tiDb.SavedOrderBatches.AnyAsync()) return;

            try
            {
                var now = DateTime.Now;
                var sampleBatches = new List<TiDbBatch>
                {
                    new TiDbBatch
                    {
                        BatchName = "BATCH-20250811-001",
                        CreatedAt = now.AddDays(-10),
                        TotalItems = 2,
                        TotalAmount = 3450.00m,
                        Items = new List<TiDbItem>
                        {
                            new TiDbItem { ProductCode = "BEARING-6204-ZZ", ProductName = "ลูกปืนตลับ SKF 6204-ZZ", Unit = "ตัว", UnitPrice = 185.00m, Quantity = 10, Remarks = "สำหรับบำรุงรักษาเครื่องกลึง T-101", IsReceived = true, ReceiveDate = now.AddDays(-8) },
                            new TiDbItem { ProductCode = "BELT-B-65", ProductName = "สายพานพัดลม B-65 Mitsuboshi", Unit = "เส้น", UnitPrice = 320.00m, Quantity = 5, Remarks = "เปลี่ยนตามรอบซ่อมบำรุง", IsReceived = true, ReceiveDate = now.AddDays(-7) }
                        }
                    },
                    new TiDbBatch
                    {
                        BatchName = "BATCH-20250811-002",
                        CreatedAt = now.AddDays(-7),
                        TotalItems = 2,
                        TotalAmount = 6100.00m,
                        Items = new List<TiDbItem>
                        {
                            new TiDbItem { ProductCode = "VALVE-SOL-24V", ProductName = "โซลินอยด์วาล์ว 24VDC SMC", Unit = "ตัว", UnitPrice = 2450.00m, Quantity = 2, Remarks = "งานซ่อมด่วนไลน์ผลิต A", IsReceived = false },
                            new TiDbItem { ProductCode = "PU-TUBE-8MM", ProductName = "ท่อนิวเมติก PU 8mm (ม้วน 100m)", Unit = "ม้วน", UnitPrice = 1200.00m, Quantity = 1, Remarks = "เดินท่อลมใหม่ไลน์ A", IsReceived = true, ReceiveDate = now.AddDays(-5) }
                        }
                    },
                    new TiDbBatch
                    {
                        BatchName = "BATCH-20250811-003",
                        CreatedAt = now.AddDays(-5),
                        TotalItems = 2,
                        TotalAmount = 17900.00m,
                        Items = new List<TiDbItem>
                        {
                            new TiDbItem { ProductCode = "GREASE-HIGH-TEMP", ProductName = "จารบีทนความร้อน SKF LGMT 3/1", Unit = "กระป๋อง", UnitPrice = 850.00m, Quantity = 4, Remarks = "สั่งซื้อสำรองคลังช่างประจำเดือน", IsReceived = false },
                            new TiDbItem { ProductCode = "OIL-HYD-68", ProductName = "น้ำมันไฮดรอลิก PTT ISO VG 68 (ถัง 200L)", Unit = "ถัง", UnitPrice = 14500.00m, Quantity = 1, Remarks = "เปลี่ยนถ่ายประจำปีเครื่องปั๊ม", IsReceived = false }
                        }
                    },
                    new TiDbBatch
                    {
                        BatchName = "BATCH-20250811-004",
                        CreatedAt = now.AddDays(-3),
                        TotalItems = 2,
                        TotalAmount = 15400.00m,
                        Items = new List<TiDbItem>
                        {
                            new TiDbItem { ProductCode = "MOTOR-3HP-3P", ProductName = "มอเตอร์ไฟฟ้า Mitsubishi 3HP 380V", Unit = "เครื่อง", UnitPrice = 8900.00m, Quantity = 1, Remarks = "สำหรับประกอบสายพานลำเลียงใหม่", IsReceived = true, ReceiveDate = now.AddDays(-1) },
                            new TiDbItem { ProductCode = "GEAR-REDUCER-1-30", ProductName = "เกียร์ทดรอบ อัตราส่วน 1:30", Unit = "ตัว", UnitPrice = 6500.00m, Quantity = 1, Remarks = "ใช้คู่กับมอเตอร์ 3HP", IsReceived = true, ReceiveDate = now.AddDays(-1) }
                        }
                    },
                    new TiDbBatch
                    {
                        BatchName = "BATCH-20250811-005",
                        CreatedAt = now.AddDays(-1),
                        TotalItems = 2,
                        TotalAmount = 3150.00m,
                        Items = new List<TiDbItem>
                        {
                            new TiDbItem { ProductCode = "BOLT-M12-50-SS", ProductName = "น็อตสแตนเลส M12x50mm (กล่อง 100 ตัว)", Unit = "กล่อง", UnitPrice = 750.00m, Quantity = 3, Remarks = "ประกอบยึดแท่นเครื่องจักร T-99", IsReceived = false },
                            new TiDbItem { ProductCode = "WASHER-M12-SS", ProductName = "แหวนสปริงสแตนเลส M12 (กล่อง 500 ตัว)", Unit = "กล่อง", UnitPrice = 450.00m, Quantity = 2, Remarks = "งานประกอบเครื่องจักร T-99", IsReceived = false }
                        }
                    }
                };

                await tiDb.SavedOrderBatches.AddRangeAsync(sampleBatches);
                await tiDb.SaveChangesAsync();
                Console.WriteLine("[DB Init] Successfully seeded 5 sample staff order batches into TiDbContext.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Init] Error seeding staff orders: {ex.Message}");
            }
        }

        private static async Task SeedIdentityUsersAndRolesAsync(
            IServiceProvider serviceProvider,
            IWebHostEnvironment env,
            IConfiguration configuration)
        {
            var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            // Create roles
            string[] roles = { "Admin", "Staff", "Dev" };
            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            string adminPassword = configuration["Seed:AdminPassword"]
                                    ?? Environment.GetEnvironmentVariable("ADMIN_PASSWORD")
                                    ?? "admin1234";

            string staffPassword = configuration["Seed:StaffPassword"]
                                    ?? Environment.GetEnvironmentVariable("STAFF_PASSWORD")
                                    ?? "123456";

            string devPassword = configuration["Seed:DevPassword"]
                                  ?? Environment.GetEnvironmentVariable("DEV_PASSWORD")
                                  ?? "dev1234";

            // Seed ADMIN01
            var adminUser = await userManager.FindByNameAsync("ADMIN01");
            if (adminUser == null)
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
                    Console.WriteLine("[DB Init] Seeded user ADMIN01 with role Admin.");
                }
                else
                {
                    Console.WriteLine($"[DB Init] Failed to seed ADMIN01: {string.Join(", ", result.Errors.Select(e => e.Description))}");
                }
            }
            else
            {
                adminUser.FullName = "แมวกวนๆ";
                adminUser.ProfilePictureUrl = "https://cdn.readawrite.com/articles/11729/11728659/thumbnail/large.gif?1";
                await userManager.UpdateAsync(adminUser);

                // Ensure admin password is valid
                var token = await userManager.GeneratePasswordResetTokenAsync(adminUser);
                await userManager.ResetPasswordAsync(adminUser, token, adminPassword);

                if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                    Console.WriteLine("[DB Init] Re-assigned role Admin to ADMIN01.");
                }
            }

            // Seed STAFF01
            var staffUser = await userManager.FindByNameAsync("STAFF01");
            if (staffUser == null)
            {
                var staff = new ApplicationUser
                {
                    UserName = "STAFF01",
                    EmployeeCode = "STAFF01",
                    FullName = "เจ้าหน้าที่ฝ่ายช่าง",
                    ProfilePictureUrl = "https://png.pngtree.com/png-clipart/20240304/original/pngtree-repairman-worker-cat-sticker-png-image_14504417.png",
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                var result = await userManager.CreateAsync(staff, staffPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(staff, "Staff");
                    Console.WriteLine("[DB Init] Seeded user STAFF01 with role Staff.");
                }
                else
                {
                    Console.WriteLine($"[DB Init] Failed to seed STAFF01: {string.Join(", ", result.Errors.Select(e => e.Description))}");
                }
            }
            else
            {
                staffUser.FullName = "เจ้าหน้าที่ฝ่ายช่าง";
                staffUser.ProfilePictureUrl = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcRkt942qIdGSgY_RotRR9_HhY3bveTRjSgZZYygIyA-3JzTkNbA9PR1CbXB&s=10";
                await userManager.UpdateAsync(staffUser);

                // Ensure staff password is valid
                var token = await userManager.GeneratePasswordResetTokenAsync(staffUser);
                await userManager.ResetPasswordAsync(staffUser, token, staffPassword);

                if (!await userManager.IsInRoleAsync(staffUser, "Staff"))
                {
                    await userManager.AddToRoleAsync(staffUser, "Staff");
                    Console.WriteLine("[DB Init] Re-assigned role Staff to STAFF01.");
                }
            }

            // Seed DEV01
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
                    Console.WriteLine("[DB Init] Seeded user DEV01 with role Dev.");
                }
                else
                {
                    Console.WriteLine($"[DB Init] Failed to seed DEV01: {string.Join(", ", result.Errors.Select(e => e.Description))}");
                }
            }
            else
            {
                devUser.FullName = "ทีมงาน Dev Test";
                await userManager.UpdateAsync(devUser);

                // Ensure dev password is valid
                var token = await userManager.GeneratePasswordResetTokenAsync(devUser);
                await userManager.ResetPasswordAsync(devUser, token, devPassword);

                if (!await userManager.IsInRoleAsync(devUser, "Dev"))
                {
                    await userManager.AddToRoleAsync(devUser, "Dev");
                    Console.WriteLine("[DB Init] Re-assigned role Dev to DEV01.");
                }

                try
                {
                    var devRoles = await userManager.GetRolesAsync(devUser);
                    if (devRoles.Contains("Admin") && !devRoles.Contains("Dev"))
                    {
                        await userManager.RemoveFromRoleAsync(devUser, "Admin");
                        await userManager.AddToRoleAsync(devUser, "Dev");
                        Console.WriteLine("[DB Init] Migrated DEV01 role: Admin -> Dev.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB Init] Warning checking DEV01 roles: {ex.Message}");
                }
            }
        }

        private static async Task SeedStockItemsFromExcelAsync(AppDbContext db, IWebHostEnvironment env)
        {
            if (await db.StockItems.AnyAsync())
                return; // Already seeded

            var excelPath = Path.Combine(env.ContentRootPath, "Data", "Product.xlsx");
            if (!File.Exists(excelPath))
            {
                Console.WriteLine($"[DB Init] Warning: Product.xlsx not found at {excelPath}");
                return;
            }

            Console.WriteLine("[DB Init] Seeding StockItems from Product.xlsx...");
            
            try
            {
                using var stream = new FileStream(excelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                
                if (worksheet == null) return;

                var rowCount = worksheet.LastRowUsed()?.RowNumber() ?? 0;
                var itemsToInsert = new List<StockItem>();

                // Assuming headers are on row 1, data starts at row 2
                for (int row = 2; row <= rowCount; row++)
                {
                    var productCode = worksheet.Cell(row, 1).GetString().Trim();
                    if (string.IsNullOrEmpty(productCode)) continue;

                    var productName = worksheet.Cell(row, 2).GetString().Trim();
                    var categoryRaw = worksheet.Cell(row, 3).GetString().Trim();
                    
                    var category = categoryRaw switch
                    {
                        "b7392108" => "อะไหล่",
                        "b453465a" => "อะไหล่เวียน",
                        _ => categoryRaw
                    };
                    
                    // FilePath is in column 4
                    var filePath = worksheet.Cell(row, 4).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(filePath)) filePath = null;

                    // InitialStock is in column 5
                    var initStr = worksheet.Cell(row, 5).GetString().Trim();
                    if (!decimal.TryParse(initStr, out decimal initialStock)) initialStock = 0;

                    // MinStock is in column 6
                    var minStr = worksheet.Cell(row, 6).GetString().Trim();
                    if (!decimal.TryParse(minStr, out decimal minStock)) minStock = 0;

                    // MaxStock is in column 7
                    var maxStr = worksheet.Cell(row, 7).GetString().Trim();
                    if (!decimal.TryParse(maxStr, out decimal maxStock)) maxStock = 0;
                    
                    // VS_สต๊อกปัจจุบัน is in column 8
                    var qtyStr = worksheet.Cell(row, 8).GetString().Trim();
                    if (!decimal.TryParse(qtyStr, out decimal quantity))
                    {
                        quantity = 0; // Default to 0 if NaN or empty
                    }

                    // VS_สถานะสต๊อก is in column 9
                    var status = worksheet.Cell(row, 9).GetString().Trim();

                    var stockItem = new StockItem
                    {
                        ProductCode = productCode,
                        ProductName = productName,
                        Category = category,
                        FilePath = filePath,
                        InitialStock = initialStock,
                        Quantity = quantity,
                        MinStock = minStock,
                        MaxStock = maxStock,
                        StockStatus = status,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    
                    itemsToInsert.Add(stockItem);
                }

                if (itemsToInsert.Any())
                {
                    // Create an initial log for the seed
                    var logs = itemsToInsert.Select(i => new StockLog
                    {
                        StockItemCode = i.ProductCode,
                        Action = "INITIAL_IMPORT",
                        QuantityChanged = i.Quantity,
                        Remarks = "Imported from Product.xlsx",
                        Timestamp = DateTime.UtcNow,
                        User = "System"
                    }).ToList();

                    await db.StockItems.AddRangeAsync(itemsToInsert);
                    await db.StockLogs.AddRangeAsync(logs);
                    await db.SaveChangesAsync();
                    Console.WriteLine($"[DB Init] Successfully seeded {itemsToInsert.Count} StockItems.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Init] Failed to seed StockItems: {ex.Message}");
            }
        }

        private static async Task SanitizeMonthlyOrderActionsAsync(AppDbContext db)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET MonthYear = SUBSTR(MonthYear, 1, 7) WHERE LENGTH(MonthYear) > 7;");
                await db.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET DeferredFromMonth = SUBSTR(DeferredFromMonth, 1, 7) WHERE DeferredFromMonth IS NOT NULL AND LENGTH(DeferredFromMonth) > 7;");

                await db.Database.ExecuteSqlRawAsync(@"
                    DELETE FROM MonthlyOrderActions
                    WHERE Id NOT IN (
                        SELECT Id FROM (
                            SELECT Id,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY OrderTrackingMasterId, MonthYear 
                                       ORDER BY CreatedAt DESC, Id DESC
                                   ) as rn
                            FROM MonthlyOrderActions
                        ) AS t
                        WHERE t.rn = 1
                    );");
                Console.WriteLine("[DB Init] Verified and sanitized MonthlyOrderActions MonthYear formats.");
            }
            catch (Exception dbEx)
            {
                Console.WriteLine($"[DB Init] DB sanitize warning: {dbEx.Message}");
            }
        }
    }
}
