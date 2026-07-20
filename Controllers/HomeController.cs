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

namespace CostFlow.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly AppDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public HomeController(AppDbContext context, UserManager<ApplicationUser> userManager,
            IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task<IActionResult> Index()
        {
            bool isAdmin = User.IsInRole("Admin");
            if (!isAdmin)
            {
                return RedirectToAction("Index", "ProductSearch");
            }
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

            int totalReferencePrices = await _context.ProductPrices.CountAsync();

            // ดึงจำนวนรายการสั่งซื้อจาก Google Sheets (Apps Script) แทน DB
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
            catch { /* ถ้า Sheets ไม่ตอบ แสดง 0 แทน */ }
            
            // Query จาก Reports table แทน
            var groupedReports = await _context.Reports
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new ReportSummaryViewModel
                {
                    ReportName = r.ReportName,
                    TotalRows = r.TotalPOs,
                    MatchedRows = r.MatchedPOs,
                    CreatedAt = r.CreatedAt,
                    CompareFileName = r.OriginalFileName
                })
                .ToListAsync();

            int totalMergedReports = groupedReports.Count;
            double avgSuccessRate = 0;
            var recentReports = groupedReports.Take(3).ToList();

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

            // คำนวณยอดรวมค่าใช้จ่ายประจำปี (Yearly Cost)
            int currentYear = DateTime.Now.Year;
            string yearPrefix = $"{currentYear:0000}-";
            decimal totalYearlyCost = await _context.MonthlyOrderActions
                .Where(a => a.MonthYear.StartsWith(yearPrefix))
                .SumAsync(a => (decimal?)a.ActionPrice) ?? 0m;

            if (totalYearlyCost == 0)
            {
                totalYearlyCost = await _context.MonthlyOrderActions.SumAsync(a => (decimal?)a.ActionPrice) ?? 0m;
            }

            var viewModel = new HomeDashboardViewModel
            {
                TotalReferencePrices = totalReferencePrices,
                TotalSparePartOrders = totalSparePartOrders,
                TotalMergedReports = totalMergedReports,
                AvgMatchSuccessRate = avgSuccessRate,
                TotalYearlyCost = totalYearlyCost,
                RecentReports = recentReports
            };

            return View(viewModel);
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

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
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
