using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
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

        public HomeController(AppDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

            int totalReferencePrices = await _context.ProductPrices.CountAsync();
            
            int totalSparePartOrders = 0;
            if (isAdmin || string.IsNullOrEmpty(currentUserId))
            {
                totalSparePartOrders = await _context.SparePartOrders.CountAsync();
            }
            else
            {
                totalSparePartOrders = await _context.SparePartOrders
                    .Where(o => _context.SparePartOrderBatches.Any(b => b.Id == o.BatchId && b.UserId == currentUserId))
                    .CountAsync();
            }
            
            var querySessions = _context.ImportSessions.AsQueryable();
            if (!isAdmin && !string.IsNullOrEmpty(currentUserId))
            {
                querySessions = querySessions.Where(s => s.UserId == currentUserId);
            }

            var sessions = await querySessions
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            int totalMergedReports = sessions.Count;
            double avgSuccessRate = 0;
            var recentReports = new System.Collections.Generic.List<ReportSummaryViewModel>();

            if (totalMergedReports > 0)
            {
                double totalAccuracy = 0;
                foreach (var session in sessions)
                {
                    int totalRows = session.MatchedCount + session.UnmatchedCount;
                    int matchedRows = session.MatchedCount;
                    double accuracy = totalRows > 0 ? (double)matchedRows / totalRows * 100 : 0;
                    totalAccuracy += accuracy;

                    if (recentReports.Count < 3)
                    {
                        recentReports.Add(new ReportSummaryViewModel
                        {
                            SessionId = session.Id,
                            ReportName = session.SourceFileName + " & " + session.CompareFileName,
                            CreatedAt = session.CreatedAt,
                            TotalRows = totalRows,
                            MatchedRows = matchedRows
                        });
                    }
                }
                avgSuccessRate = Math.Round(totalAccuracy / totalMergedReports, 1);
            }

            var viewModel = new HomeDashboardViewModel
            {
                TotalReferencePrices = totalReferencePrices,
                TotalSparePartOrders = totalSparePartOrders,
                TotalMergedReports = totalMergedReports,
                AvgMatchSuccessRate = avgSuccessRate,
                RecentReports = recentReports
            };

            return View(viewModel);
        }

        public IActionResult Privacy()
        {
            return View();
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
        public System.Collections.Generic.List<ReportSummaryViewModel> RecentReports { get; set; } = new();
    }
}
