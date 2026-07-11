using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using ClosedXML.Excel;
using System.IO;

namespace CostFlow.Controllers
{
    [Authorize]
    public class ReportController : Controller
    {
        private readonly AppDbContext _context;

        public ReportController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            // Query จาก Report table แทน
            var reports = await _context.Reports
                .Include(r => r.Orders)
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

            return View(reports);
        }

        public async Task<IActionResult> Details(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return NotFound();

            var report = await _context.Reports
                .Include(r => r.Orders)
                    .ThenInclude(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                .FirstOrDefaultAsync(r => r.ReportName == fileName);

            if (report == null) return NotFound();

            var orders = report.Orders
                .OrderBy(o => o.Status == "Pending" ? 0 : 1)
                .ThenBy(o => o.PoNumber)
                .ToList();

            ViewData["ReportName"] = report.ReportName;
            ViewData["CreatedAt"] = report.CreatedAt;

            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteReport(string fileName)
        {
            var report = await _context.Reports
                .Include(r => r.Orders)
                .FirstOrDefaultAsync(r => r.ReportName == fileName);

            if (report != null)
            {
                _context.Reports.Remove(report); // Cascade delete จะลบ Orders ด้วย
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }

            return Json(new { success = false, error = "ไม่พบข้อมูลที่ต้องการลบ" });
        }

        public async Task<IActionResult> ExportToExcel(string fileName, string? customName)
        {
            if (string.IsNullOrEmpty(fileName)) return NotFound();

            var report = await _context.Reports
                .Include(r => r.Orders)
                .FirstOrDefaultAsync(r => r.ReportName == fileName);

            if (report == null) return NotFound();

            var orders = report.Orders
                .OrderBy(o => o.Status == "Pending" ? 0 : 1)
                .ThenBy(o => o.PoNumber)
                .ToList();

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("TrackingReport");

            // Define headers
            worksheet.Cell(1, 1).Value = "เลขที่ใบสั่งซื้อ (PO)";
            worksheet.Cell(1, 2).Value = "วันที่สั่ง";
            worksheet.Cell(1, 3).Value = "วันที่อนุมัติ";
            worksheet.Cell(1, 4).Value = "ปภ.ความเร่งด่วน";
            worksheet.Cell(1, 5).Value = "จำนวนเงิน";
            worksheet.Cell(1, 6).Value = "หมายเหตุ";
            worksheet.Cell(1, 7).Value = "จำนวนชิ้น (สกัดได้)";
            worksheet.Cell(1, 8).Value = "สถานะ";

            var headerRow = worksheet.Range("A1:H1");
            headerRow.Style.Font.Bold = true;
            headerRow.Style.Fill.BackgroundColor = XLColor.LightGray;

            // Fill data
            for (int i = 0; i < orders.Count; i++)
            {
                var order = orders[i];
                var row = i + 2;

                worksheet.Cell(row, 1).Value = order.PoNumber;
                worksheet.Cell(row, 2).Value = order.RequestDate;
                worksheet.Cell(row, 3).Value = order.ApprovedDate;
                worksheet.Cell(row, 4).Value = order.Urgency;
                worksheet.Cell(row, 5).Value = order.Amount;
                worksheet.Cell(row, 6).Value = order.Remarks;
                worksheet.Cell(row, 7).Value = order.RemarksQuantity;
                worksheet.Cell(row, 8).Value = order.Status == "Matched" ? "ได้แผนผลิตแล้ว" : "ยังไม่มีแผนผลิต";
            }

            worksheet.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var content = stream.ToArray();

            string downloadName = !string.IsNullOrWhiteSpace(customName)
                ? $"{customName.Trim()}.xlsx"
                : $"TrackingReport_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

            return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", downloadName);
        }

        [HttpGet]
        public async Task<IActionResult> GetWeeklyPlanDetail(Guid orderId)
        {
            // 1. Try to find OrderTrackingMaster
            var order = await _context.OrderTrackingMasters
                .Include(o => o.MatchedInWeeklyPlans)
                    .ThenInclude(wpd => wpd.WeeklyPlan)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order != null)
            {
                if (!order.MatchedInWeeklyPlans.Any())
                {
                    return Json(new
                    {
                        success = true,
                        department = "-",
                        orderName = order.Remarks ?? "-",
                        orderStatus = order.Status ?? "Pending",
                        deliveryTarget = "-",
                        fileName = "-",
                        poNumberInFile = order.PoNumber ?? "",
                        weeklyPlanPrice = "-",
                        masterPrice = order.Amount ?? "-",
                        history = new List<object>()
                    });
                }

                // Get matching details sorted by latest UploadedAt
                var sortedDetails = order.MatchedInWeeklyPlans
                    .OrderByDescending(wpd => wpd.WeeklyPlan.UploadedAt)
                    .ToList();

                var latestDetail = sortedDetails.First();
                var latestPlan = latestDetail.WeeklyPlan;

                var history = sortedDetails.Select(wpd => new
                {
                    fileName = wpd.WeeklyPlan?.FileName ?? "-",
                    sheetName = wpd.WeeklyPlan?.SheetName ?? "-",
                    department = wpd.Department ?? "-",
                    orderName = wpd.OrderName ?? "-",
                    orderStatus = wpd.OrderStatus ?? "-",
                    deliveryTarget = wpd.DeliveryTarget ?? "-",
                    price = wpd.Price ?? "-",
                    uploadedAt = wpd.WeeklyPlan != null ? wpd.WeeklyPlan.UploadedAt.ToString("dd/MM/yyyy HH:mm") : "-"
                }).ToList();

                return Json(new
                {
                    success = true,
                    department = latestDetail.Department ?? "-",
                    orderName = latestDetail.OrderName ?? "-",
                    orderStatus = latestDetail.OrderStatus ?? "-",
                    deliveryTarget = latestDetail.DeliveryTarget ?? "-",
                    fileName = latestPlan?.FileName ?? "-",
                    poNumberInFile = latestDetail.PoNumberInFile ?? "",
                    weeklyPlanPrice = latestDetail.Price ?? "-",
                    masterPrice = order.Amount ?? "-",
                    history = history
                });
            }

            // 2. Try to find WeeklyPlanDetail (unmatched ones)
            var wpdDetail = await _context.WeeklyPlanDetails
                .Include(d => d.WeeklyPlan)
                .FirstOrDefaultAsync(d => d.Id == orderId);

            if (wpdDetail != null)
            {
                var history = new List<object>
                {
                    new
                    {
                        fileName = wpdDetail.WeeklyPlan?.FileName ?? "-",
                        sheetName = wpdDetail.WeeklyPlan?.SheetName ?? "-",
                        department = wpdDetail.Department ?? "-",
                        orderName = wpdDetail.OrderName ?? "-",
                        orderStatus = wpdDetail.OrderStatus ?? "-",
                        deliveryTarget = wpdDetail.DeliveryTarget ?? "-",
                        price = wpdDetail.Price ?? "-",
                        uploadedAt = wpdDetail.WeeklyPlan != null ? wpdDetail.WeeklyPlan.UploadedAt.ToString("dd/MM/yyyy HH:mm") : "-"
                    }
                };

                return Json(new
                {
                    success = true,
                    department = wpdDetail.Department ?? "-",
                    orderName = wpdDetail.OrderName ?? "-",
                    orderStatus = wpdDetail.OrderStatus ?? "-",
                    deliveryTarget = wpdDetail.DeliveryTarget ?? "-",
                    fileName = wpdDetail.WeeklyPlan?.FileName ?? "-",
                    poNumberInFile = wpdDetail.PoNumberInFile ?? "",
                    weeklyPlanPrice = wpdDetail.Price ?? "-",
                    masterPrice = "-",
                    history = history
                });
            }

            return Json(new { success = false, error = "ไม่พบข้อมูลแผนผลิต" });
        }

        public async Task<IActionResult> GetAllSystemOrders(
            int page = 1, 
            int pageSize = 15, 
            string? search = null, 
            string? reportName = null, 
            string? statusFilter = "All")
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 15;

            var allItems = new List<dynamic>();

            // 1. Query Master Orders if status filter is not exclusively "Unmatched"
            if (statusFilter != "Unmatched")
            {
                var masterQuery = _context.OrderTrackingMasters
                    .Include(o => o.Report)
                    .Include(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(reportName))
                {
                    masterQuery = masterQuery.Where(o => o.Report.ReportName == reportName);
                }

                if (!string.IsNullOrEmpty(search))
                {
                    var s = search.Trim().ToLower();
                    masterQuery = masterQuery.Where(o => (o.PoNumber ?? "").ToLower().Contains(s) || 
                                                     (o.Remarks ?? "").ToLower().Contains(s) || 
                                                     (o.Report.ReportName ?? "").ToLower().Contains(s) ||
                                                     o.MatchedInWeeklyPlans.Any(wpd => (wpd.WeeklyPlan.FileName ?? "").ToLower().Contains(s)));
                }

                if (statusFilter == "Matched" || statusFilter == "Pending")
                {
                    masterQuery = masterQuery.Where(o => o.Status == statusFilter);
                }

                var masterList = await masterQuery
                    .Select(o => new
                    {
                        Id = o.Id,
                        PoNumber = o.PoNumber ?? "",
                        RequestDate = o.RequestDate ?? "-",
                        ApprovedDate = o.ApprovedDate ?? "-",
                        Urgency = o.Urgency ?? "-",
                        Amount = o.Amount ?? "-",
                        ProposedPrice = o.MatchedInWeeklyPlans
                            .OrderByDescending(wpd => wpd.WeeklyPlan.UploadedAt)
                            .Select(wpd => wpd.Price ?? "-")
                            .FirstOrDefault() ?? "-",
                        Remarks = o.Remarks ?? "-",
                        Status = o.Status ?? "Pending",
                        ReportName = o.Report.ReportName ?? "-",
                        WeeklyPlanFiles = o.MatchedInWeeklyPlans
                            .Select(wpd => wpd.WeeklyPlan.FileName ?? "-")
                            .Distinct()
                            .ToList(),
                        SortDate = o.CreatedAt
                    })
                    .ToListAsync();

                allItems.AddRange(masterList);
            }

            // 2. Query Unmatched Weekly Plan Details if status filter is "All" or "Unmatched"
            if (statusFilter == "All" || statusFilter == "Unmatched")
            {
                var weeklyQuery = _context.WeeklyPlanDetails
                    .Include(d => d.WeeklyPlan)
                        .ThenInclude(wp => wp.Report)
                    .Where(d => d.IsMatched == false || d.MatchedOrderId == null)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(reportName))
                {
                    weeklyQuery = weeklyQuery.Where(d => d.WeeklyPlan.Report.ReportName == reportName);
                }

                if (!string.IsNullOrEmpty(search))
                {
                    var s = search.Trim().ToLower();
                    weeklyQuery = weeklyQuery.Where(d => (d.PoNumberInFile ?? "").ToLower().Contains(s) || 
                                                     (d.OrderName ?? "").ToLower().Contains(s) || 
                                                     (d.WeeklyPlan.Report.ReportName ?? "").ToLower().Contains(s) ||
                                                     (d.WeeklyPlan.FileName ?? "").ToLower().Contains(s));
                }

                var weeklyList = await weeklyQuery
                    .Select(d => new
                    {
                        Id = d.Id,
                        PoNumber = d.PoNumberInFile ?? "",
                        RequestDate = "-",
                        ApprovedDate = "-",
                        Urgency = "-",
                        Amount = "-",
                        ProposedPrice = d.Price ?? "-",
                        Remarks = d.OrderName ?? "-",
                        Status = "Unmatched",
                        ReportName = d.WeeklyPlan.Report.ReportName ?? "-",
                        WeeklyPlanFiles = new List<string> { d.WeeklyPlan.FileName ?? "-" },
                        SortDate = d.WeeklyPlan.UploadedAt
                    })
                    .ToListAsync();

                allItems.AddRange(weeklyList);
            }

            // 3. Merge in memory, Order & Paginate
            var orderedItems = allItems
                .OrderByDescending(x => (DateTime)x.SortDate)
                .ThenBy(x => (string)x.PoNumber)
                .ToList();

            int totalCount = orderedItems.Count;
            var items = orderedItems
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Json(new
            {
                success = true,
                totalCount,
                page,
                pageSize,
                items
            });
        }

        [HttpGet]
        public IActionResult AllOrders(string? returnReportName)
        {
            ViewBag.ReturnReportName = returnReportName;
            return View();
        }
    }
}
