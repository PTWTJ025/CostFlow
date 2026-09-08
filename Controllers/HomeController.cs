using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CostFlow.Data;
using CostFlow.Models;
using Microsoft.AspNetCore.Identity;
using System.IO;
using System.IO.Compression;

namespace CostFlow.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly AppDbContext _context;
        private readonly TiDbContext _tiContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public HomeController(AppDbContext context, TiDbContext tiContext, UserManager<ApplicationUser> userManager,
            IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _tiContext = tiContext;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        private static bool _mySqlTablesChecked = false;

        private async Task<HomeDashboardViewModel> BuildDashboardViewModelAsync()
        {
            if (!_mySqlTablesChecked && _context.Database.IsMySql())
            {
                try
                {
                    await DatabaseInitializer.EnsureMySqlTablesExistAsync(_context, _tiContext);
                    _mySqlTablesChecked = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Dashboard] Warning ensuring MySQL tables: {ex.Message}");
                }
            }

            int totalReferencePrices = 0;
            try
            {
                totalReferencePrices = await _context.ProductPrices.CountAsync();
            }
            catch { }

            int totalSparePartOrders = 0;
            try
            {
                string? appScriptUrl = _configuration["GoogleSheets:PrimarySyncAppScriptUrl"] ?? _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
                if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                {
                    var client = _httpClientFactory.CreateClient("GoogleAppsScript");
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var response = await client.GetAsync(appScriptUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("batches", out var batchesEl))
                        {
                            foreach (var b in batchesEl.EnumerateArray())
                            {
                                if (b.TryGetProperty("TotalItems", out var ti))
                                    totalSparePartOrders += ti.GetInt32();
                            }
                        }
                    }
                }
            }
            catch
            {
                /* ถ้า Sheets ไม่ตอบ แสดง 0 แทน */
            }

            var allReports = new List<CostFlow.Models.Report>();
            try
            {
                allReports = await _context.Reports.ToListAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Warning fetching reports: {ex.Message}");
            }

            int totalMergedReports = allReports.Count;
            double avgSuccessRate = 0;

            if (totalMergedReports > 0)
            {
                double totalAccuracy = 0;
                foreach (var report in allReports)
                {
                    double accuracy = report.TotalPOs > 0 ? (double)report.MatchedPOs / report.TotalPOs * 100 : 0;
                    totalAccuracy += accuracy;
                }
                avgSuccessRate = Math.Round(totalAccuracy / totalMergedReports, 1);
            }

            var groupedReports = new List<ReportSummaryViewModel>();
            var now = DateTime.Now;
            for (int i = 0; i < 3; i++)
            {
                var targetMonth = now.AddMonths(-i);
                var monthReports = allReports.Where(r => r.CreatedAt.Year == targetMonth.Year && r.CreatedAt.Month == targetMonth.Month).ToList();
                
                var date = new DateTime(targetMonth.Year, targetMonth.Month, 1, 0, 0, 0, DateTimeKind.Local);
                groupedReports.Add(new ReportSummaryViewModel
                {
                    ReportName = $"สรุปประจำเดือน {date.ToString("MMMM yyyy", new System.Globalization.CultureInfo("th-TH"))}",
                    TotalRows = monthReports.Sum(r => r.TotalPOs),
                    MatchedRows = monthReports.Sum(r => r.MatchedPOs),
                    CreatedAt = date,
                    CreatedBy = "ระบบ",
                    CompareFileName = ""
                });
            }

            int currentYear = DateTime.Now.Year;
            string yearPrefix = $"{currentYear:0000}-";
            decimal totalYearlyCost = 0m;
            try
            {
                totalYearlyCost = await _context.MonthlyOrderActions
                    .Where(a => a.MonthYear.StartsWith(yearPrefix))
                    .SumAsync(a => (decimal?)a.ActionPrice) ?? 0m;

                if (totalYearlyCost == 0)
                {
                    totalYearlyCost = await _context.MonthlyOrderActions.SumAsync(a => (decimal?)a.ActionPrice) ?? 0m;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dashboard] Warning: Could not query MonthlyOrderActions: {ex.Message}");
            }

            return new HomeDashboardViewModel
            {
                TotalReferencePrices = totalReferencePrices,
                TotalSparePartOrders = totalSparePartOrders,
                TotalMergedReports = totalMergedReports,
                AvgMatchSuccessRate = avgSuccessRate,
                TotalYearlyCost = totalYearlyCost,
                RecentReports = groupedReports.Take(3).ToList()
            };
        }

        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            string userName = User.Identity?.Name ?? user?.UserName ?? "";
            bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Dev") ||
                           userName.Equals("ADMIN01", StringComparison.OrdinalIgnoreCase) ||
                           userName.Equals("DEV01", StringComparison.OrdinalIgnoreCase);

            if (!isAdmin)
            {
                return RedirectToAction("Index", "ProductSearch");
            }

            var viewModel = await BuildDashboardViewModelAsync();
            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> GetDashboardApiStats()
        {
            var viewModel = await BuildDashboardViewModelAsync();

            var recentReportsFormatted = viewModel.RecentReports?.Select(r => new
            {
                reportName = r.ReportName,
                totalRows = r.TotalRows,
                matchedRows = r.MatchedRows,
                createdAt = r.CreatedAt,
                formattedDate =
                    "ประจำเดือน " + r.CreatedAt.ToString("MMMM yyyy", new System.Globalization.CultureInfo("th-TH")) + " • รวบรวมโดย " + (r.CreatedBy ?? "ไม่ระบุ"),
                accuracy = r.TotalRows > 0 ? Math.Round((double)r.MatchedRows / r.TotalRows * 100, 1) : 0,
                detailsUrl = "/Report"
            }).ToList();

            return Json(new
            {
                totalMergedReports = viewModel.TotalMergedReports,
                totalYearlyCost = viewModel.TotalYearlyCost,
                formattedTotalYearlyCost = viewModel.TotalYearlyCost.ToString("N0"),
                totalSparePartOrders = viewModel.TotalSparePartOrders,
                totalReferencePrices = viewModel.TotalReferencePrices,
                recentReports = recentReportsFormatted
            });
        }


        // GET: /Home/Settings
        [HttpGet]
        public async Task<IActionResult> Settings()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login", "Account");
            }

            var roles = await _userManager.GetRolesAsync(user);
            ViewData["RoleName"] = roles.FirstOrDefault() ?? "Staff";

            return View(user);
        }

        // POST: /Home/SaveSettings
        [HttpPost]
        public async Task<IActionResult> SaveSettings(string fullName, string email)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Json(new { success = false, error = "ไม่พบผู้ใช้งาน" });
            }

            if (string.IsNullOrWhiteSpace(fullName))
            {
                return Json(new { success = false, error = "กรุณากรอกชื่อ-นามสกุล" });
            }

            user.FullName = fullName.Trim();
            user.Email = email?.Trim();

            var result = await _userManager.UpdateAsync(user);
            if (result.Succeeded)
            {
                return Json(new { success = true });
            }

            return Json(new { success = false, error = "เกิดข้อผิดพลาดในการบันทึกข้อมูล" });
        }

        // POST: /Home/BackupDatabase
        // ดาวน์โหลดข้อมูลสำรองทั้ง SQLite (.db) และ TiDB Cloud (.json) รวมกันเป็นไฟล์ .ZIP สำหรับ Admin
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        public async Task<IActionResult> BackupDatabase()
        {
            try
            {
                // 1. ตรวจสอบประเภท DefaultConnection (SQLite หรือ TiDB MySQL)
                string? connStr = _configuration.GetConnectionString("DefaultConnection");
                string? dbRelativePath = null;
                bool isSqlite = false;

                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    foreach (var part in connStr.Split(';'))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                        {
                            dbRelativePath = trimmed.Substring("Data Source=".Length).Trim();
                            isSqlite = true;
                            break;
                        }
                    }
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                byte[]? appDbJsonBytes = null;
                byte[]? stockJsonBytes = null;
                string? dbFullPath = null;

                if (isSqlite && !string.IsNullOrWhiteSpace(dbRelativePath))
                {
                    dbFullPath = System.IO.Path.IsPathRooted(dbRelativePath)
                        ? dbRelativePath
                        : System.IO.Path.Combine(Directory.GetCurrentDirectory(), dbRelativePath);
                }
                else
                {
                    // ถ้าต่อ TiDB Cloud ให้ดึงข้อมูลตารางหลักออกมาเป็น JSON สำหรับ Backup (ครอบคลุม 100% ทุกตาราง)
                    try
                    {
                        var productPrices = await _context.ProductPrices.AsNoTracking().ToListAsync();
                        var reports = await _context.Reports.AsNoTracking().ToListAsync();
                        var orders = await _context.OrderTrackingMasters.AsNoTracking().ToListAsync();
                        var plans = await _context.WeeklyPlans.Include(p => p.Details).AsNoTracking().ToListAsync();
                        var actions = await _context.MonthlyOrderActions.AsNoTracking().ToListAsync();

                        // 1.1 ดึงตารางระบบสต็อกสินค้า (StockItems, ItemMappings, StockLogs)
                        var stockItems = await _context.StockItems.AsNoTracking().ToListAsync();
                        var itemMappings = await _context.ItemMappings.AsNoTracking().ToListAsync();
                        var stockLogs = await _context.StockLogs.AsNoTracking().ToListAsync();

                        // 1.2 ดึงตารางบัญชีผู้ใช้งานและบทบาท (Users & Roles)
                        var users = await _context.Users.AsNoTracking().Select(u => new
                        {
                            u.Id,
                            u.UserName,
                            u.Email,
                            u.FullName,
                            u.EmployeeCode,
                            u.IsActive,
                            u.ProfilePictureUrl,
                            u.CreatedAt
                        }).ToListAsync();

                        var roles = await _context.Roles.AsNoTracking().Select(r => new
                        {
                            r.Id,
                            r.Name,
                            r.NormalizedName
                        }).ToListAsync();

                        var userRoles = await _context.UserRoles.AsNoTracking().ToListAsync();

                        var coreData = new
                        {
                            BackupTime = DateTime.UtcNow,
                            DatabaseSource = "TiDB Cloud (costflow_db)",
                            Coverage = "100% Full Schema",
                            ProductPrices = productPrices,
                            Reports = reports,
                            Orders = orders,
                            WeeklyPlans = plans,
                            MonthlyOrderActions = actions,
                            StockItems = stockItems,
                            ItemMappings = itemMappings,
                            StockLogs = stockLogs,
                            Users = users,
                            Roles = roles,
                            UserRoles = userRoles
                        };

                        var opt = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
                        };
                        string coreJson = JsonSerializer.Serialize(coreData, opt);
                        appDbJsonBytes = System.Text.Encoding.UTF8.GetBytes(coreJson);

                        // สร้างไฟล์ JSON สำหรับระบบสต็อกโดยเฉพาะเพื่อการตรวจสอบที่สะดวก
                        var stockData = new
                        {
                            BackupTime = DateTime.UtcNow,
                            DatabaseSource = "TiDB Cloud (costflow_db)",
                            TotalStockItems = stockItems.Count,
                            TotalItemMappings = itemMappings.Count,
                            TotalStockLogs = stockLogs.Count,
                            StockItems = stockItems,
                            ItemMappings = itemMappings,
                            StockLogs = stockLogs
                        };
                        string stockJson = JsonSerializer.Serialize(stockData, opt);
                        stockJsonBytes = System.Text.Encoding.UTF8.GetBytes(stockJson);
                    }
                    catch (Exception appDbEx)
                    {
                        Console.WriteLine($"[BackupDatabase] Warning: Could not export core TiDB data: {appDbEx.Message}");
                    }
                }

                // 2. ดึงข้อมูลจาก TiDB Cloud (Saved Orders - test database)
                byte[]? tidbJsonBytes = null;
                try
                {
                    var batches = await _tiContext.SavedOrderBatches
                        .Include(b => b.Items)
                        .AsNoTracking()
                        .ToListAsync();

                    var tidbData = batches.Select(b => new
                    {
                        b.Id,
                        b.BatchName,
                        b.CreatedAt,
                        b.TotalItems,
                        b.TotalAmount,
                        Items = b.Items.Select(i => new
                        {
                            i.Id,
                            i.BatchId,
                            i.ProductCode,
                            i.ProductName,
                            i.Quantity,
                            i.UnitPrice,
                            i.Unit,
                            i.Remarks,
                            i.IsReceived,
                            i.ReceiveDate
                        })
                    });

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    string jsonString = JsonSerializer.Serialize(tidbData, options);
                    tidbJsonBytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
                }
                catch (Exception tiEx)
                {
                    Console.WriteLine($"[BackupDatabase] Warning: Could not fetch TiDB data: {tiEx.Message}");
                }

                // 3. รวมฐานข้อมูลเข้าเป็นไฟล์ .ZIP (ครอบคลุม 100% ทุกตาราง)
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        // ไฟล์ 1: SQLite .db หรือ TiDB_CostFlowCore_*.json (ข้อมูลระบบหลัก + สต็อก + บัญชีผู้ใช้)
                        if (isSqlite && dbFullPath != null && System.IO.File.Exists(dbFullPath))
                        {
                            var dbEntry = archive.CreateEntry($"CostFlow_sqlite_{timestamp}.db", CompressionLevel.Optimal);
                            using (var entryStream = dbEntry.Open())
                            using (var fileStream = System.IO.File.OpenRead(dbFullPath))
                            {
                                await fileStream.CopyToAsync(entryStream);
                            }
                        }
                        else if (appDbJsonBytes != null)
                        {
                            var coreEntry = archive.CreateEntry($"TiDB_CostFlowCore_{timestamp}.json", CompressionLevel.Optimal);
                            using (var entryStream = coreEntry.Open())
                            {
                                await entryStream.WriteAsync(appDbJsonBytes, 0, appDbJsonBytes.Length);
                            }
                        }

                        // ไฟล์ 2: TiDB_StockSystem_*.json (ระบบคลังและประวัติการเคลื่อนไหวสต็อก)
                        if (stockJsonBytes != null && stockJsonBytes.Length > 0)
                        {
                            var stockEntry = archive.CreateEntry($"TiDB_StockSystem_{timestamp}.json", CompressionLevel.Optimal);
                            using (var entryStream = stockEntry.Open())
                            {
                                await entryStream.WriteAsync(stockJsonBytes, 0, stockJsonBytes.Length);
                            }
                        }

                        // ไฟล์ 3: TiDB_SavedOrders_*.json (ประวัติสั่งซื้ออะไหล่จาก TiDB test)
                        if (tidbJsonBytes != null && tidbJsonBytes.Length > 0)
                        {
                            var tidbEntry = archive.CreateEntry($"TiDB_SavedOrders_{timestamp}.json", CompressionLevel.Optimal);
                            using (var entryStream = tidbEntry.Open())
                            {
                                await entryStream.WriteAsync(tidbJsonBytes, 0, tidbJsonBytes.Length);
                            }
                        }
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    string zipDownloadName = $"CostFlow_FullBackup_{timestamp}.zip";
                    return File(ms.ToArray(), "application/zip", zipDownloadName);
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาดในการสำรองข้อมูล: {ex.Message}" });
            }
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        // POST: /Home/ArchiveAndPurge
        // สำรองข้อมูลที่เก่าเกิน RetentionMonths ลง Google Sheets แล้วลบออกจาก DB
        // POST: /Home/SyncAllToGoogleSheets
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        public async Task<IActionResult> SyncAllToGoogleSheets([FromServices] CostFlow.Services.MonthlyOrderSyncService syncService)
        {
            var result = await syncService.SyncAllMonthsToGoogleSheetsAsync("Manual Admin Trigger from Settings Page");
            if (result.Success)
            {
                return Json(new { success = true, message = result.Message, totalActions = result.TotalActions, totalMonths = result.TotalMonths, details = result.Details });
            }
            else
            {
                return Json(new { success = false, error = result.Message, details = result.Details });
            }
        }

        //
        // [เทสผ่าน Postman]
        // POST /Home/ArchiveAndPurge?cutoffOverride=2025-01-01
        // — ถ้าใส่ cutoffOverride จะใช้วันนั้นเป็นวันตัดข้อมูลแทนค่า config
        // — ตัวอย่าง: ถ้าอยากจำลองว่า "วันนี้คือ 5 นาทีหลังครบ 2 ปี" ให้ใส่วันที่ล่วงหน้าไป
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        [AcceptVerbs("GET", "POST")]
        public async Task<IActionResult> ArchiveAndPurge(string? cutoffOverride = null)
        {
            try
            {
                int retentionMonths = int.TryParse(_configuration["Archive:RetentionMonths"], out var rm) ? rm : 24;

                // ตรวจสอบว่ามีการส่ง cutoffOverride มาไหม (โหมดเทส)
                DateTime cutoffDate;
                bool isTestMode = false;
                if (!string.IsNullOrWhiteSpace(cutoffOverride) &&
                    DateTime.TryParse(cutoffOverride, out var overrideDate))
                {
                    cutoffDate = overrideDate.ToUniversalTime();
                    isTestMode = true;
                    Console.WriteLine(
                        $"[ArchiveAndPurge] ⚠️ TEST MODE — cutoffOverride = {cutoffDate:yyyy-MM-dd HH:mm} UTC");
                }
                else
                {
                    cutoffDate = DateTime.UtcNow.AddMonths(-retentionMonths); // วันตัดข้อมูลเก่าจริง (ตาม config)
                    Console.WriteLine(
                        $"[ArchiveAndPurge] cutoffDate = {cutoffDate:yyyy-MM-dd} (retention = {retentionMonths} เดือน)");
                }

                // -------------------------------------------------------------------
                // [ชีทที่ 1] WeeklyPlan_Matching — ดึง WeeklyPlan + WeeklyPlanDetail
                // ที่อัปโหลดก่อนวันตัด และ Report ที่เกี่ยวข้องไม่มีรายการ MonthlyOrderAction
                // ที่ยังค้างอยู่ในช่วง 2 ปีที่ผ่านมา (เพื่อความปลอดภัย)
                // -------------------------------------------------------------------
                var oldPlans = await _context.WeeklyPlans
                    .Include(w => w.Details)
                    .ThenInclude(d => d.MatchedOrder)
                    .Include(w => w.Report)
                    .Where(w => w.UploadedAt < cutoffDate)
                    .ToListAsync();

                // Flatten ทุก Detail ออกมาเป็นแถวๆ พร้อมคำนวณ MonthYear จาก ApprovedDate ของ MatchedOrder หรือ PO Number
                var flatPlanItems = oldPlans
                    .SelectMany(plan => plan.Details.Select(d =>
                    {
                        string poNo = d.MatchedOrder?.PoNumber ?? d.PoNumberInFile ?? "";

                        // ลำดับความสำคัญในการระบุเดือน: ApprovedDate -> PO Number (เช่น WO2507.. -> 2025-07) -> DeliveryTarget -> UploadedAt
                        string monthKey = GetMonthYearFromDateStr(d.MatchedOrder?.ApprovedDate)
                                          ?? GetMonthYearFromDateStr(null, poNo)
                                          ?? GetMonthYearFromDateStr(d.DeliveryTarget)
                                          ?? plan.UploadedAt.ToLocalTime().ToString("yyyy-MM");

                        // แสดงวันที่อนุมัติ (ถ้าไม่มี ให้สกัดจาก PO Number เป็น dd/MM/yyyy พ.ศ. แทนที่จะโชว์ -)
                        string approvedDateDisplay = d.MatchedOrder?.ApprovedDate ?? "";
                        if (string.IsNullOrWhiteSpace(approvedDateDisplay) || approvedDateDisplay == "-")
                        {
                            var extractedMKey = GetMonthYearFromDateStr(null, poNo);
                            if (!string.IsNullOrEmpty(extractedMKey) && extractedMKey.Contains("-"))
                            {
                                var parts = extractedMKey.Split('-');
                                if (parts.Length == 2 && int.TryParse(parts[0], out int y) &&
                                    int.TryParse(parts[1], out int m))
                                {
                                    approvedDateDisplay = $"01/{m:00}/{y + 543}";
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(approvedDateDisplay)) approvedDateDisplay = "-";

                        return new
                        {
                            MonthYear = monthKey,
                            PoNo = poNo,
                            Row = new object?[]
                            {
                                plan.UploadedAt.ToLocalTime().ToString("dd/MM/yyyy"), // A วันที่อัปโหลด
                                approvedDateDisplay, // B วันที่อนุมัติ
                                plan.FileName, // C ชื่อไฟล์อ้างอิง
                                poNo, // D เลข PO
                                CleanText(d.OrderName ?? d.MatchedOrder?.Remarks), // E ชื่อสินค้า
                                d.Department ?? "-", // F แผนก
                                d.DeliveryTarget ?? "-", // G กำหนดส่งมอบ
                                d.OrderStatus ?? "-", // H สถานะในไฟล์แผน
                                d.IsMatched ? "จับคู่สำเร็จ" : "ไม่พบ PO นี้ในระบบ" // I ผลการจับคู่
                            }
                        };
                    })).ToList();

                var planGroups = flatPlanItems
                    .GroupBy(item => item.MonthYear)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        MonthYear = g.Key,
                        Rows = g.OrderBy(x => x.PoNo).Select(x => x.Row).ToList()
                    }).ToList();

                int totalPlanRows = planGroups.Sum(g => g.Rows.Count);

                // -------------------------------------------------------------------
                // [ชีทที่ 2] MonthlyCost_Actions — ดึง OrderTrackingMaster และ Action ทั้งหมดที่จะถูกลบ
                // (ต้องมั่นใจว่าทุก PO ใน OrderTrackingMaster ถูกสำรองลง Sheet ก่อนโดนลบเสมอ)
                // -------------------------------------------------------------------
                var cutoffMonthKey = cutoffDate.ToString("yyyy-MM");

                // ดึง OrderTrackingMasters เก่าทั้งหมดที่มีสิทธิ์ถูกลบ
                var oldOrdersToArchive = await _context.OrderTrackingMasters
                    .Include(o => o.Report)
                    .Include(o => o.MatchedInWeeklyPlans)
                    .ThenInclude(w => w.WeeklyPlan)
                    .Where(o => o.CreatedAt < cutoffDate)
                    .ToListAsync();

                var oldOrderIdsToArchive = oldOrdersToArchive.Select(o => o.Id).ToHashSet();

                // ดึง MonthlyOrderActions เก่าทั้งหมด
                var oldActions = await _context.MonthlyOrderActions
                    .Include(a => a.OrderTrackingMaster)
                    .Where(a => string.Compare(a.MonthYear, cutoffMonthKey) < 0 ||
                                oldOrderIdsToArchive.Contains(a.OrderTrackingMasterId))
                    .ToListAsync();

                // แมป Action ตาม OrderTrackingMasterId
                var actionsByOrderId = oldActions
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // รวบรวมทุกแถวข้อมูลลงในลิสต์เดี่ยว โดยสกัด MonthYear จริงของแต่ละแถว
                var formattedActionItems = new List<(string MonthYear, string PoNo, object?[] Row)>();

                foreach (var order in oldOrdersToArchive)
                {
                    var latestPlan = order.MatchedInWeeklyPlans
                        .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                        .FirstOrDefault();

                    // คำนวณเดือนปี (yyyy-MM) ของ PO นี้จาก ApprovedDate หรือ PO Number หรือ CreatedAt
                    string orderMonthKey = GetMonthYearFromDateStr(order.ApprovedDate) ??
                                           GetMonthYearFromDateStr(null, order.PoNumber) ??
                                           order.CreatedAt.ToLocalTime().ToString("yyyy-MM");

                    // แสดงวันที่อนุมัติ (ถ้าไม่มี ให้สกัดจาก PO Number เป็น dd/MM/yyyy พ.ศ. แทนที่จะโชว์ -)
                    string approvedDateDisplay = order.ApprovedDate ?? "";
                    if (string.IsNullOrWhiteSpace(approvedDateDisplay) || approvedDateDisplay == "-")
                    {
                        var extractedMKey = GetMonthYearFromDateStr(null, order.PoNumber);
                        if (!string.IsNullOrEmpty(extractedMKey) && extractedMKey.Contains("-"))
                        {
                            var parts = extractedMKey.Split('-');
                            if (parts.Length == 2 && int.TryParse(parts[0], out int y) &&
                                int.TryParse(parts[1], out int m))
                            {
                                approvedDateDisplay = $"01/{m:00}/{y + 543}";
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(approvedDateDisplay)) approvedDateDisplay = "-";

                    if (actionsByOrderId.TryGetValue(order.Id, out var orderActions) && orderActions.Count > 0)
                    {
                        foreach (var act in orderActions)
                        {
                            var row = new object?[]
                            {
                                act.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), // A วัน/เวลาที่บันทึก
                                approvedDateDisplay, // B วันที่อนุมัติ
                                order.PoNumber ?? "-", // C เลข PO
                                CleanText(order.Remarks), // D ชื่อสินค้า
                                latestPlan?.Department ?? order.Urgency ?? "-", // E แผนก
                                latestPlan?.DeliveryTarget ?? "-", // F กำหนดส่งมอบ
                                FormatMoneyStr(order.Amount), // G ยอดสั่งซื้อเต็ม
                                act.Action switch // H สถานะการรับของ
                                {
                                    "ReceivedFull" => "รับของครบแล้ว",
                                    "Deferred" => "ผ่อนชำระ",
                                    "Skipped" => "ข้าม / ยังไม่รับ",
                                    _ => act.Action
                                },
                                act.ActionPrice.ToString("N2"), // I ยอดที่จ่ายจริง
                                act.DeferredFromMonth ?? "-" // J ยกยอดมาจาก
                            };
                            formattedActionItems.Add((act.MonthYear ?? orderMonthKey, order.PoNumber ?? "", row));
                        }
                    }
                    else
                    {
                        // ถ้า PO นี้ยังไม่เคยมี Action เลย -> สำรองบรรทัด PO สถานะ "ยังไม่ดำเนินการ" เพื่อให้ข้อมูลอยู่ครบใน Sheet
                        var defaultRow = new object?[]
                        {
                            order.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), // A วัน/เวลาที่บันทึก
                            approvedDateDisplay, // B วันที่อนุมัติ
                            order.PoNumber ?? "-", // C เลข PO
                            CleanText(order.Remarks), // D ชื่อสินค้า
                            latestPlan?.Department ?? order.Urgency ?? "-", // E แผนก
                            latestPlan?.DeliveryTarget ?? "-", // F กำหนดส่งมอบ
                            FormatMoneyStr(order.Amount), // G ยอดสั่งซื้อเต็ม
                            "ยังไม่ดำเนินการ", // H สถานะการรับของ
                            "0.00", // I ยอดที่จ่ายจริง
                            "-" // J ยกยอดมาจาก
                        };
                        formattedActionItems.Add((orderMonthKey, order.PoNumber ?? "", defaultRow));
                    }
                }

                var actionGroups = formattedActionItems
                    .GroupBy(item => item.MonthYear)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        MonthYear = g.Key,
                        Rows = g.OrderBy(x => x.PoNo).Select(x => x.Row).ToList()
                    }).ToList();

                int totalActionRows = actionGroups.Sum(g => g.Rows.Count);

                // -------------------------------------------------------------------
                // [ชีทที่ 3] ประวัติสต๊อกย้อนหลัง (Archive) — ดึง StockLogs ที่เก่าเกิน 2 ปี
                // เพื่อสำรองลง Google Sheet ก่อนลบออกจาก TiDB เพื่อรักษาความเร็วของระบบสต็อก
                // -------------------------------------------------------------------
                var oldStockLogs = await _context.StockLogs
                    .Where(l => l.Timestamp < cutoffDate)
                    .OrderBy(l => l.Timestamp)
                    .ToListAsync();

                // ดึงชื่อสินค้าสำหรับแสดงผลใน Sheet
                var stockCodeMap = await _context.StockItems
                    .AsNoTracking()
                    .ToDictionaryAsync(s => s.ProductCode, s => s.ProductName);

                var stockLogRows = oldStockLogs.Select(l =>
                {
                    string pName = stockCodeMap.TryGetValue(l.StockItemCode, out var name) ? name : "-";
                    string actionDisplay = l.Action switch
                    {
                        "IN_WO" => "รับเข้าจาก PO/สั่งผลิต",
                        "IN_MANUAL" => "รับเข้าคลัง (Manual)",
                        "OUT_MANUAL" => "เบิก/จ่ายออก",
                        "INITIAL_IMPORT" => "ยอดยกมาเริ่มต้น",
                        "ADJUST" => "ปรับปรุงยอด",
                        _ => l.Action
                    };

                    string qtyFormatted = l.QuantityChanged >= 0 
                        ? $"+{l.QuantityChanged:N0}" 
                        : l.QuantityChanged.ToString("N0");

                    return new object?[]
                    {
                        l.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), // A วัน/เวลาที่ทำรายการ
                        l.StockItemCode,                                            // B รหัสสินค้า
                        CleanText(pName),                                           // C ชื่อสินค้า / รายการอะไหล่
                        actionDisplay,                                              // D ประเภทรายการ
                        qtyFormatted,                                               // E จำนวนที่เปลี่ยนแปลง
                        l.ReferenceId ?? "-",                                       // F เลขอ้างอิง
                        l.User ?? "-",                                              // G ผู้บันทึกรายการ
                        CleanText(l.Remarks) ?? "-"                                 // H หมายเหตุ
                    };
                }).ToList();

                // -------------------------------------------------------------------
                // ดึง Report เก่าทั้งหมดตาม cutoffDate (โดยตรงจาก Report.CreatedAt)
                // -------------------------------------------------------------------
                var oldReports = await _context.Reports
                    .Where(r => r.CreatedAt < cutoffDate)
                    .ToListAsync();
                var oldReportIds = oldReports.Select(r => r.Id).ToList();

                // ตรวจสอบว่ามีข้อมูลอะไรที่ต้องประมวลผลไหม (ทั้งสำหรับ Sheet และสำหรับ Purge)
                bool hasAnythingToProcess = totalPlanRows > 0 || totalActionRows > 0 || stockLogRows.Count > 0
                                                              || oldReports.Count > 0 || oldOrdersToArchive.Count > 0;

                if (!hasAnythingToProcess)
                {
                    return Json(new
                    {
                        success = true, message = $"ไม่มีข้อมูลที่อายุเกิน {retentionMonths} เดือนในระบบ", archived = 0,
                        purged = 0
                    });
                }

                // -------------------------------------------------------------------
                // [ZIP ARCHIVE] สร้างไฟล์ .ZIP บรรจุ JSON แยกตามตาราง 5 ไฟล์ในหน่วยความจำ
                // ก่อนเริ่มการลบ เพื่อความปลอดภัยสูงสุด (Fail-Safe 100%)
                // -------------------------------------------------------------------
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string zipFileName = $"CostFlow_PurgedArchive_{timestamp}.zip";

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                // 1. Metadata
                var archiveMetadata = new
                {
                    ArchivedAt = DateTime.UtcNow,
                    ArchivedAtLocal = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                    CutoffDateUtc = cutoffDate,
                    CutoffDateDisplay = cutoffDate.ToString("dd/MM/yyyy HH:mm") + (isTestMode ? " (TEST MODE)" : ""),
                    RetentionMonths = retentionMonths,
                    IsTestMode = isTestMode,
                    Operator = User.Identity?.Name ?? "Admin",
                    DatabaseSource = "TiDB Cloud (costflow_db)",
                    Summary = new
                    {
                        WeeklyPlanRows = totalPlanRows,
                        MonthlyCostActionRows = totalActionRows,
                        StockLogsCount = oldStockLogs.Count,
                        ReportsCount = oldReports.Count,
                        OrdersCount = oldOrdersToArchive.Count
                    }
                };

                // 2. StockLogs Data
                var stockLogsArchiveData = new
                {
                    Title = "ประวัติสต๊อกย้อนหลังที่เก่าเกินกำหนด (Purged Stock Logs)",
                    ArchivedAt = DateTime.UtcNow,
                    CutoffDate = cutoffDate,
                    TotalCount = oldStockLogs.Count,
                    RowsFormatted = stockLogRows,
                    Logs = oldStockLogs.Select(l => new
                    {
                        l.Id,
                        TimestampUtc = l.Timestamp,
                        TimestampLocal = l.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                        l.StockItemCode,
                        ProductName = stockCodeMap.TryGetValue(l.StockItemCode, out var name) ? name : "-",
                        l.Action,
                        l.QuantityChanged,
                        l.ReferenceId,
                        l.User,
                        l.Remarks
                    })
                };

                // 3. MonthlyCost Data
                var monthlyCostArchiveData = new
                {
                    Title = "บันทึกการรับของและค่าใช้จ่ายที่เก่าเกินกำหนด (Purged Monthly Cost Actions)",
                    ArchivedAt = DateTime.UtcNow,
                    CutoffDate = cutoffDate,
                    TotalRows = totalActionRows,
                    ActionGroups = actionGroups,
                    RawActions = oldActions.Select(a => new
                    {
                        a.Id,
                        a.OrderTrackingMasterId,
                        a.MonthYear,
                        a.Action,
                        a.ActionPrice,
                        a.DeferredFromMonth,
                        a.IsForcedPayment,
                        a.CreatedAt,
                        a.UpdatedAt
                    })
                };

                // 4. WeeklyPlans Data
                var weeklyPlansArchiveData = new
                {
                    Title = "ใบสั่งผลิตและแผนการผลิตที่เก่าเกินกำหนด (Purged Weekly Plans)",
                    ArchivedAt = DateTime.UtcNow,
                    CutoffDate = cutoffDate,
                    TotalRows = totalPlanRows,
                    PlanGroups = planGroups,
                    Plans = oldPlans.Select(p => new
                    {
                        p.Id,
                        p.FileName,
                        p.UploadedAt,
                        p.ReportId,
                        DetailsCount = p.Details.Count,
                        Details = p.Details.Select(d => new
                        {
                            d.Id,
                            d.PoNumberInFile,
                            d.OrderName,
                            d.Department,
                            d.DeliveryTarget,
                            d.OrderStatus,
                            d.IsMatched,
                            MatchedOrderId = d.MatchedOrderId
                        })
                    })
                };

                // 5. Orders & Reports Data
                var ordersReportsArchiveData = new
                {
                    Title = "ข้อมูลรายงานและรายการคำสั่งซื้อหลักที่เก่าเกินกำหนด (Purged Orders & Reports)",
                    ArchivedAt = DateTime.UtcNow,
                    CutoffDate = cutoffDate,
                    TotalReports = oldReports.Count,
                    TotalOrders = oldOrdersToArchive.Count,
                    Reports = oldReports.Select(r => new
                    {
                        r.Id,
                        r.ReportName,
                        r.CreatedAt,
                        r.CreatedBy
                    }),
                    Orders = oldOrdersToArchive.Select(o => new
                    {
                        o.Id,
                        o.ReportId,
                        o.PoNumber,
                        o.Remarks,
                        o.Amount,
                        o.ApprovedDate,
                        o.Urgency,
                        o.CreatedAt
                    })
                };

                // สร้างก้อน .ZIP ใน Memory Stream
                byte[] zipBytes;
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        void AddJsonEntry(string entryName, object data)
                        {
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            using var writer = new StreamWriter(entryStream, System.Text.Encoding.UTF8);
                            writer.Write(JsonSerializer.Serialize(data, jsonOptions));
                        }

                        AddJsonEntry("Archive_Metadata.json", archiveMetadata);
                        AddJsonEntry("Archive_StockLogs.json", stockLogsArchiveData);
                        AddJsonEntry("Archive_MonthlyCost.json", monthlyCostArchiveData);
                        AddJsonEntry("Archive_WeeklyPlans.json", weeklyPlansArchiveData);
                        AddJsonEntry("Archive_Orders_Reports.json", ordersReportsArchiveData);
                    }
                    zipBytes = ms.ToArray();
                }

                string zipBase64 = Convert.ToBase64String(zipBytes);

                // -------------------------------------------------------------------
                // [PURGE] ยืนยันแล้วว่า ZIP สร้างสำเร็จสมบูรณ์ 100%
                // ดำเนินการลบข้อมูลใน TiDB ภายใต้ Database Execution Transaction
                // ลำดับ: WeeklyPlanDetails → WeeklyPlans → MonthlyOrderActions → StockLogs
                //         → (SaveChanges) → OrderTrackingMasters → Reports
                // -------------------------------------------------------------------
                int remainingOldPlansCount = 0;
                int remainingActionsCount = 0;
                int remainingOldOrdersCount = 0;

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // 1. ดึง WeeklyPlan ที่เชื่อมกับ Report เก่า (ที่ยังเหลืออยู่)
                    var remainingOldPlans = oldReportIds.Count > 0
                        ? await _context.WeeklyPlans
                            .Where(w => oldReportIds.Contains(w.ReportId) || w.UploadedAt < cutoffDate)
                            .ToListAsync()
                        : await _context.WeeklyPlans.Where(w => w.UploadedAt < cutoffDate).ToListAsync();
                    var remainingOldPlanIds = remainingOldPlans.Select(p => p.Id).ToList();
                    remainingOldPlansCount = remainingOldPlanIds.Count;

                    // 2. ลบ WeeklyPlanDetails ที่ยังเหลืออยู่
                    if (remainingOldPlanIds.Count > 0)
                    {
                        var remainingDetails = await _context.WeeklyPlanDetails
                            .Where(d => remainingOldPlanIds.Contains(d.WeeklyPlanId))
                            .ToListAsync();
                        _context.WeeklyPlanDetails.RemoveRange(remainingDetails);
                        _context.WeeklyPlans.RemoveRange(remainingOldPlans);
                    }

                    // 3. ดึง OrderTrackingMasters ที่เชื่อมกับ Report เก่า
                    var remainingOldOrders = oldReportIds.Count > 0
                        ? await _context.OrderTrackingMasters
                            .Where(o => oldReportIds.Contains(o.ReportId))
                            .ToListAsync()
                        : new List<OrderTrackingMaster>();
                    var remainingOldOrderIds = remainingOldOrders.Select(o => o.Id).ToList();
                    remainingOldOrdersCount = remainingOldOrders.Count;

                    // 4. ดึง MonthlyOrderActions ที่ยังเหลืออยู่
                    var remainingActions = await _context.MonthlyOrderActions
                        .Where(a => string.Compare(a.MonthYear, cutoffMonthKey) < 0 || (remainingOldOrderIds.Count > 0 &&
                            remainingOldOrderIds.Contains(a.OrderTrackingMasterId)))
                        .ToListAsync();
                    remainingActionsCount = remainingActions.Count;

                    if (remainingActions.Count > 0)
                    {
                        _context.MonthlyOrderActions.RemoveRange(remainingActions);
                    }

                    // 5. ลบ StockLogs เก่าเกิน 2 ปีออกจาก TiDB
                    if (oldStockLogs.Count > 0)
                    {
                        _context.StockLogs.RemoveRange(oldStockLogs);
                    }

                    await _context.SaveChangesAsync(); // save รอบแรก (child tables & logs)

                    // 6. ลบ OrderTrackingMasters และ Reports (parent tables)
                    if (remainingOldOrders.Count > 0)
                    {
                        _context.OrderTrackingMasters.RemoveRange(remainingOldOrders);
                    }

                    if (oldReports.Count > 0)
                    {
                        _context.Reports.RemoveRange(oldReports);
                    }

                    await _context.SaveChangesAsync(); // save รอบสอง (parent tables)

                    // ยืนยันการเปลี่ยนแปลงข้อมูลลง Database
                    await transaction.CommitAsync();
                }
                catch (Exception dbEx)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"[ArchiveAndPurge] ❌ Transaction rolled back: {dbEx.Message}");
                    return Json(new
                    {
                        success = false,
                        error = $"เกิดข้อผิดพลาดขณะล้างข้อมูล: {dbEx.Message} — ระบบได้ยกเลิกคำสั่ง (Rollback) ข้อมูลทั้งหมดยังคงอยู่ในฐานข้อมูล TiDB อย่างปลอดภัย"
                    });
                }

                int totalArchived = totalPlanRows + totalActionRows + oldStockLogs.Count;
                int totalPurged = remainingOldPlansCount + remainingActionsCount + remainingOldOrdersCount +
                                  oldReports.Count + oldStockLogs.Count;

                return Json(new
                {
                    success = true,
                    testMode = isTestMode,
                    message = isTestMode
                        ? $"[TEST MODE] สำรองและลบข้อมูลก่อนวันที่ {cutoffDate:dd/MM/yyyy} เรียบร้อยแล้ว (สร้างไฟล์ {zipFileName} พร้อมดาวน์โหลด)"
                        : $"สำรองข้อมูลลงไฟล์ .ZIP และลบข้อมูลเก่าเรียบร้อยแล้ว (รวมทั้งหมด {totalPurged:N0} รายการ)",
                    archived = totalArchived,
                    purged = totalPurged,
                    zipBase64 = zipBase64,
                    zipFileName = zipFileName,
                    details = new
                    {
                        weeklyPlanRows = totalPlanRows,
                        monthlyCostRows = totalActionRows,
                        stockLogRows = oldStockLogs.Count,
                        reportsPurged = oldReports.Count,
                        ordersPurged = remainingOldOrdersCount,
                        stockLogsPurged = oldStockLogs.Count,
                        cutoffDate = cutoffDate.ToString("dd/MM/yyyy HH:mm") + (isTestMode ? " (TEST)" : "")
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาด: {ex.Message}" });
            }
        }

        private static string? GetMonthYearFromDateStr(string? dateStr, string? poNumber = null)
        {
            if (!string.IsNullOrWhiteSpace(dateStr) && dateStr != "-")
            {
                var dt = ParseDate(dateStr);
                if (dt.HasValue)
                {
                    return dt.Value.ToString("yyyy-MM");
                }
            }

            // ถ้า Date แกะไม่ได้ ให้ลองแกะจาก PO Number เช่น WO26040031 -> 2026-04
            if (!string.IsNullOrWhiteSpace(poNumber))
            {
                var cleanPo = poNumber.Trim().ToUpper();
                if (cleanPo.StartsWith("WO") && cleanPo.Length >= 6)
                {
                    var yearStr = cleanPo.Substring(2, 2); // "26" -> 2569 -> 2026
                    var monthStr = cleanPo.Substring(4, 2); // "04" -> April
                    if (int.TryParse(yearStr, out int y2) && int.TryParse(monthStr, out int m) && m >= 1 && m <= 12)
                    {
                        int fullYear = y2 > 50 ? (y2 + 2500 - 543) : (y2 + 2000);
                        return $"{fullYear:0000}-{m:00}";
                    }
                }
            }

            return null;
        }

        private static DateTime? ParseDate(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();
            if (input == "-") return null;
            if (input.Contains(' ')) input = input.Split(' ')[0];
            input = input.Replace('-', '/');

            var parts = input.Split('/');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var p1) &&
                int.TryParse(parts[1], out var p2) &&
                int.TryParse(parts[2], out var p3))
            {
                int day = p1;
                int month = p2;
                int year = p3;

                // Format yyyy/MM/dd
                if (p1 > 1000)
                {
                    year = p1;
                    month = p2;
                    day = p3;
                }
                else
                {
                    // Check if month & day are swapped (MM/dd/yyyy vs dd/MM/yyyy)
                    if (p2 > 12 && p1 <= 12)
                    {
                        day = p2;
                        month = p1;
                    }

                    // Convert year
                    if (year > 2500) year -= 543;
                    else if (year > 50 && year < 100) year = (year + 2500) - 543; // e.g. 69 -> 2569 -> 2026
                    else if (year < 50) year += 2000; // e.g. 26 -> 2026
                }

                try
                {
                    return new DateTime(year, month, day);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string FormatMoneyStr(string? amountStr)
        {
            if (string.IsNullOrWhiteSpace(amountStr) || amountStr == "-") return "0.00";
            if (decimal.TryParse(amountStr.Replace(",", "").Trim(), out decimal val))
            {
                return val.ToString("N2");
            }

            return amountStr;
        }

        private static string CleanText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input) || input == "-") return "-";
            var cleaned = System.Text.RegularExpressions.Regex.Replace(input, @"\r?\n|\r", " ");
            cleaned = cleaned.Replace("สั่งทำ ", "").Replace("สั่งทำ", "").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "-" : cleaned;
        }
    }

    public class HomeDashboardViewModel
    {
        public int TotalReferencePrices { get; set; }
        public int TotalSparePartOrders { get; set; }
        public int TotalMergedReports { get; set; }
        public double AvgMatchSuccessRate { get; set; }
        public decimal TotalYearlyCost { get; set; }
        public System.Collections.Generic.List<ReportSummaryViewModel> RecentReports { get; set; } = new();
    }
}
