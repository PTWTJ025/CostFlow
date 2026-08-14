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

        private async Task<HomeDashboardViewModel> BuildDashboardViewModelAsync()
        {
            int totalReferencePrices = await _context.ProductPrices.CountAsync();

            int totalSparePartOrders = 0;
            try
            {
                string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
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

            var groupedReports = await _context.Reports
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new ReportSummaryViewModel
                {
                    ReportName = r.ReportName,
                    TotalRows = r.TotalPOs,
                    MatchedRows = r.MatchedPOs,
                    CreatedAt = r.CreatedAt,
                    CompareFileName = r.OriginalFileName,
                    CreatedBy = r.CreatedBy
                })
                .ToListAsync();

            int totalMergedReports = groupedReports.Count;
            double avgSuccessRate = 0;
            var recentReports = groupedReports.Take(3).Select(r => new
            {
                ReportName = r.ReportName,
                TotalRows = r.TotalRows,
                MatchedRows = r.MatchedRows,
                CreatedAt = r.CreatedAt,
                CreatedBy = r.CreatedBy ?? "ไม่ระบุ",
                FormattedDate =
                    r.CreatedAt.ToString("dd MMM yyyy HH:mm น.", new System.Globalization.CultureInfo("th-TH")),
                Accuracy = r.TotalRows > 0 ? Math.Round((double)r.MatchedRows / r.TotalRows * 100, 1) : 0,
                DetailsUrl = Url.Action("Details", "Report", new { fileName = r.ReportName })
            }).ToList();

            if (totalMergedReports > 0)
            {
                double totalAccuracy = 0;
                foreach (var report in groupedReports)
                {
                    double accuracy = report.TotalRows > 0 ? (double)report.MatchedRows / report.TotalRows * 100 : 0;
                    totalAccuracy += accuracy;
                }

                avgSuccessRate = Math.Round(totalAccuracy / totalMergedReports, 1);
            }

            int currentYear = DateTime.Now.Year;
            string yearPrefix = $"{currentYear:0000}-";
            decimal totalYearlyCost = await _context.MonthlyOrderActions
                .Where(a => a.MonthYear.StartsWith(yearPrefix))
                .SumAsync(a => (decimal?)a.ActionPrice) ?? 0m;

            if (totalYearlyCost == 0)
            {
                totalYearlyCost = await _context.MonthlyOrderActions.SumAsync(a => (decimal?)a.ActionPrice) ?? 0m;
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
            bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Dev");
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
                    r.CreatedAt.ToString("dd MMM yyyy HH:mm น.", new System.Globalization.CultureInfo("th-TH")),
                accuracy = r.TotalRows > 0 ? Math.Round((double)r.MatchedRows / r.TotalRows * 100, 1) : 0,
                detailsUrl = Url.Action("Details", "Report", new { fileName = r.ReportName })
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
                // 1. อ่าน path ของ SQLite database
                string? connStr = _configuration.GetConnectionString("DefaultConnection");
                string? dbRelativePath = null;

                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    foreach (var part in connStr.Split(';'))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                        {
                            dbRelativePath = trimmed.Substring("Data Source=".Length).Trim();
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(dbRelativePath))
                    return Json(new { success = false, error = "ไม่พบ path ของ SQLite database ในไฟล์ตั้งค่า" });

                string dbFullPath = System.IO.Path.IsPathRooted(dbRelativePath)
                    ? dbRelativePath
                    : System.IO.Path.Combine(Directory.GetCurrentDirectory(), dbRelativePath);

                if (!System.IO.File.Exists(dbFullPath))
                    return Json(new { success = false, error = $"ไม่พบไฟล์ฐานข้อมูล: {dbRelativePath}" });

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // 2. ดึงข้อมูลจาก TiDB Cloud
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


                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string jsonString = JsonSerializer.Serialize(tidbData, options);
                    tidbJsonBytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
                }
                catch (Exception tiEx)
                {
                    Console.WriteLine($"[BackupDatabase] Warning: Could not fetch TiDB data: {tiEx.Message}");
                }

                // 3. รวมทั้ง 2 ฐานข้อมูลเข้าเป็นไฟล์ .ZIP
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    {
                        // ไฟล์ 1: SQLite .db
                        var dbEntry = archive.CreateEntry($"CostFlow_sqlite_{timestamp}.db", CompressionLevel.Optimal);
                        using (var entryStream = dbEntry.Open())
                        using (var fileStream = System.IO.File.OpenRead(dbFullPath))
                        {
                            await fileStream.CopyToAsync(entryStream);
                        }

                        // ไฟล์ 2: TiDB .json (ถ้ามี)
                        if (tidbJsonBytes != null && tidbJsonBytes.Length > 0)
                        {
                            var tidbEntry = archive.CreateEntry($"TiDB_SavedOrders_{timestamp}.json",
                                CompressionLevel.Optimal);
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
                // อ่านค่า config
                string? archiveUrl = _configuration["GoogleSheets:ArchiveAppScriptUrl"];
                if (string.IsNullOrWhiteSpace(archiveUrl) || archiveUrl.Contains("_placeholder"))
                    return Json(new
                        { success = false, error = "ยังไม่ได้ตั้งค่า ArchiveAppScriptUrl ในไฟล์ตั้งค่าระบบ" });

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
                // ดึง Report เก่าทั้งหมดตาม cutoffDate (โดยตรงจาก Report.CreatedAt)
                // -------------------------------------------------------------------
                var oldReports = await _context.Reports
                    .Where(r => r.CreatedAt < cutoffDate)
                    .ToListAsync();
                var oldReportIds = oldReports.Select(r => r.Id).ToList();

                // ตรวจสอบว่ามีข้อมูลอะไรที่ต้องประมวลผลไหม (ทั้งสำหรับ Sheet และสำหรับ Purge)
                bool hasAnythingToProcess = totalPlanRows > 0 || totalActionRows > 0
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
                // Fail-Safe: ถ้ามีแถวข้อมูลต้องสำรอง ต้องส่งไป Google Sheets ก่อนเสมอ
                // และต้องยืนยันสำเร็จ ถึงจะทำการลบออกจาก Database
                // -------------------------------------------------------------------
                if (totalPlanRows > 0 || totalActionRows > 0)
                {
                    // รวมแถวทั้งหมดเพื่อรองรับ Apps Script เวอร์ชั่นเก่า (Backward Compatibility)
                    var allPlanRows = planGroups.SelectMany(g => g.Rows).ToList();
                    var allActionRows = actionGroups.SelectMany(g => g.Rows).ToList();

                    var payload = new
                    {
                        SheetName_Plans = "WeeklyPlan_Matching",
                        SheetName_Actions = "MonthlyCost_Actions",
                        // ส่งทั้งแบบใหม่ (Grouped) และแบบเก่า (Flat)
                        PlanGroups = planGroups,
                        ActionGroups = actionGroups,
                        planRows = allPlanRows,
                        actionRows = allActionRows
                    };

                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(120);

                    var json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(archiveUrl, content);
                    if (!response.IsSuccessStatusCode)
                        return Json(new
                        {
                            success = false,
                            error =
                                $"Google Sheets ตอบกลับ HTTP {(int)response.StatusCode} — ข้อมูลยังไม่ถูกลบออกจากระบบ"
                        });

                    var responseBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[ArchiveAndPurge] AppsScript Response: {responseBody}");

                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;
                    bool isSheetSuccess = root.TryGetProperty("success", out var succProp) && succProp.GetBoolean();

                    int archivedPlans =
                        root.TryGetProperty("archivedPlans", out var pProp) &&
                        pProp.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? pProp.GetInt32()
                            : 0;
                    int archivedActions =
                        root.TryGetProperty("archivedActions", out var aProp) &&
                        aProp.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? aProp.GetInt32()
                            : 0;
                    int totalArchivedBySheet = archivedPlans + archivedActions;

                    if (!isSheetSuccess)
                    {
                        string sheetError = root.TryGetProperty("error", out var errProp)
                            ? errProp.GetString() ?? ""
                            : "Google Sheets ตอบกลับว่าการบันทึกไม่สำเร็จ";
                        return Json(new
                            { success = false, error = $"ยกเลิกการลบ — {sheetError} — ข้อมูลยังคงอยู่ใน Database" });
                    }

                    // ป้องกันกรณี Apps Script ตอบ success = true แต่ไม่ได้ลงบันทึกจริง (เช่น 0 แถว)
                    int expectedRows = totalPlanRows + totalActionRows;
                    if (expectedRows > 0 && totalArchivedBySheet == 0)
                    {
                        return Json(new
                        {
                            success = false,
                            error =
                                $"ยกเลิกการลบ — Google Sheets บันทึกได้ 0 แถว (จากทั้งหมด {expectedRows} แถว) กรุณาตรวจสอบการตั้งค่า Apps Script Deployment"
                        });
                    }
                }

                // -------------------------------------------------------------------
                // [PURGE] ยืนยันแล้วว่า Sheet รับข้อมูลเรียบร้อย ทำการลบ DB แบบถอนรากถอนโคน
                // ลำดับ: WeeklyPlanDetails → WeeklyPlans → MonthlyOrderActions
                //         → (SaveChanges) → OrderTrackingMasters → Reports
                // -------------------------------------------------------------------

                // 1. ดึง WeeklyPlan ที่เชื่อมกับ Report เก่า (ที่ยังเหลืออยู่)
                var remainingOldPlans = oldReportIds.Count > 0
                    ? await _context.WeeklyPlans
                        .Where(w => oldReportIds.Contains(w.ReportId) || w.UploadedAt < cutoffDate)
                        .ToListAsync()
                    : await _context.WeeklyPlans.Where(w => w.UploadedAt < cutoffDate).ToListAsync();
                var remainingOldPlanIds = remainingOldPlans.Select(p => p.Id).ToList();

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

                // 4. ดึง MonthlyOrderActions ที่ยังเหลืออยู่
                var remainingActions = await _context.MonthlyOrderActions
                    .Where(a => string.Compare(a.MonthYear, cutoffMonthKey) < 0 || (remainingOldOrderIds.Count > 0 &&
                        remainingOldOrderIds.Contains(a.OrderTrackingMasterId)))
                    .ToListAsync();
                if (remainingActions.Count > 0)
                {
                    _context.MonthlyOrderActions.RemoveRange(remainingActions);
                }

                await _context.SaveChangesAsync(); // save รอบแรก (child tables)

                // 5. ลบ OrderTrackingMasters และ Reports (parent tables)
                if (remainingOldOrders.Count > 0)
                {
                    _context.OrderTrackingMasters.RemoveRange(remainingOldOrders);
                }

                if (oldReports.Count > 0)
                {
                    _context.Reports.RemoveRange(oldReports);
                }

                await _context.SaveChangesAsync(); // save รอบสอง (parent tables)

                int totalArchived = totalPlanRows + totalActionRows;
                int totalPurged = remainingOldPlanIds.Count + remainingActions.Count + remainingOldOrders.Count +
                                  oldReports.Count;

                // สร้างก้อน Backup JSON ของข้อมูลที่กำลังจะถูกลบ เพื่อให้ดาวน์โหลดเก็บไว้ในเครื่อง
                var backupPayload = new
                {
                    PurgedAt = DateTime.UtcNow,
                    CutoffDate = cutoffDate,
                    RetentionMonths = retentionMonths,
                    TotalPlanRows = totalPlanRows,
                    TotalActionRows = totalActionRows,
                    TotalReportsPurged = oldReports.Count,
                    TotalOrdersPurged = oldOrdersToArchive.Count,
                    WeeklyPlanGroups = planGroups,
                    MonthlyCostActionGroups = actionGroups,
                    PurgedReports = oldReports.Select(r => new { r.Id, r.ReportName, r.CreatedAt, r.CreatedBy }),
                    PurgedOrders = oldOrdersToArchive.Select(o => new { o.Id, o.PoNumber, o.Remarks, o.Amount, o.ApprovedDate, o.CreatedAt })
                };

                string backupJsonString = System.Text.Json.JsonSerializer.Serialize(backupPayload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string backupFileName = $"CostFlow_PurgedArchive_{DateTime.Now:yyyyMMdd_HHmmss}.json";

                return Json(new
                {
                    success = true,
                    testMode = isTestMode,
                    message = isTestMode
                        ? $"[TEST MODE] สำรองและลบข้อมูลก่อนวันที่ {cutoffDate:dd/MM/yyyy} เรียบร้อยแล้ว"
                        : "สำรองข้อมูลลง Google Sheets, ลบข้อมูลเก่า และดาวน์โหลดไฟล์สำรองเรียบร้อยแล้ว",
                    archived = totalArchived,
                    purged = totalPurged,
                    backupFileName = backupFileName,
                    backupJson = backupJsonString,
                    details = new
                    {
                        weeklyPlanRows = totalPlanRows,
                        monthlyCostRows = totalActionRows,
                        reportsPurged = oldReports.Count,
                        ordersPurged = remainingOldOrders.Count,
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
