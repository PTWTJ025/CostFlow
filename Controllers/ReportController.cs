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
    [Authorize(Roles = "Admin")]
    public class ReportController : Controller
    {
        private readonly AppDbContext _context;
        public ReportController(AppDbContext context)
        {
            _context = context;
        }
        public async Task<IActionResult> Index(int? year)
        {
            // Thai month names
            var thaiMonths = new[] { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน", 
                                     "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };

            var now = DateTime.Now;
            
            // Get all orders with ApprovedDate
            var allOrders = await _context.OrderTrackingMasters
                .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                .Select(o => new { o.Id, o.PoNumber, o.ApprovedDate, o.Amount })
                .ToListAsync();

            // หาปีที่มีข้อมูลทั้งหมดในระบบเพื่อประมวลผลช่วงปี
            var parsedYears = allOrders
                .Select(o => ParseThaiDate(o.ApprovedDate))
                .Where(d => d.HasValue)
                .Select(d => d!.Value.Year)
                .Distinct()
                .ToList();

            // กำหนดปีที่เลือก:
            // 1. ถ้ามี Query Parameter 'year' ให้ใช้ปีนั้น
            // 2. ถ้าไม่มี ให้ดึงปีล่าสุดที่มีข้อมูลในระบบ (Max Year จาก DB)
            // 3. ถ้าไม่มีข้อมูลในระบบเลย ให้ใช้ปีปัจจุบันของเครื่อง
            var selectedYear = year;
            if (!selectedYear.HasValue)
            {
                selectedYear = parsedYears.Any() ? parsedYears.Max() : now.Year;
            }

            // Parse ApprovedDate and group by month (for selected year)
            var ordersByMonth = allOrders
                .Select(o => new { 
                    o.Id, 
                    o.PoNumber, 
                    o.Amount,
                    ParsedDate = ParseThaiDate(o.ApprovedDate) 
                })
                .Where(x => x.ParsedDate.HasValue && x.ParsedDate.Value.Year == selectedYear)
                .GroupBy(x => x.ParsedDate!.Value.Month)
                .ToDictionary(g => g.Key, g => g.Select(x => new { x.Id, x.PoNumber, x.Amount }).ToList());

            // Build 12 month cards
            var cards = new List<object>();
            for (int month = 1; month <= 12; month++)
            {
                var monthKey = $"{selectedYear:0000}-{month:00}";
                var monthDisplay = $"{thaiMonths[month]} {selectedYear + 543}";
                
                var ordersInMonth = ordersByMonth.ContainsKey(month) ? ordersByMonth[month] : [];
                var totalOrders = ordersInMonth.Count;
                var totalAmount = 0m;
                
                foreach (var order in ordersInMonth)
                {
                    if (decimal.TryParse(order.Amount, out var amt))
                    {
                        totalAmount += amt;
                    }
                }

                var isCurrentMonth = (selectedYear == now.Year && month == now.Month);
                var statusCode = totalOrders == 0 ? "empty" : (isCurrentMonth ? "active" : "completed");
                var statusLabel = totalOrders == 0 ? "ไม่มีข้อมูล" : (isCurrentMonth ? "กำลังดำเนินการ" : "มีข้อมูล");

                cards.Add(new {
                    MonthYearDisplay = monthDisplay,
                    MonthYearKey = monthKey,
                    Month = month,
                    Year = selectedYear,
                    TotalOrders = totalOrders,
                    TotalAmount = totalAmount,
                    StatusCode = statusCode,
                    StatusLabel = statusLabel
                });
            }

            ViewData["SelectedYear"] = selectedYear;
            ViewData["CurrentYear"] = now.Year;

            // กำหนดช่วงปีสำหรับให้เลือกใน Dropdown:
            // - เริ่มตั้งแต่ พ.ศ. 2565 (2022) ตามที่คุณระบุว่าต้องการย้อนหลังตั้งแต่เปลี่ยนจาก Papersheet
            // - สิ้นสุดที่ปีสูงสุดระหว่าง (ปีปัจจุบันของเครื่อง, ปีสูงสุดที่มีข้อมูลใน DB, หรืออย่างน้อยที่สุดคือปี 2026/2569)
            var maxAvailableYear = Math.Max(now.Year, parsedYears.Any() ? parsedYears.Max() : 2026);
            var minAvailableYear = 2022; // พ.ศ. 2565

            var availableYears = new List<int>();
            for (int yr = maxAvailableYear; yr >= minAvailableYear; yr--)
            {
                availableYears.Add(yr);
            }
            ViewData["AvailableYears"] = availableYears;
            
            return View(cards);
        }

        private DateTime? ParseThaiDate(string? thaiDateStr)
        {
            if (string.IsNullOrWhiteSpace(thaiDateStr)) return null;
            
            // Remove extra spaces
            thaiDateStr = thaiDateStr.Trim();

            // Ignore standard placeholder '-' or empty/whitespace values without logging warnings
            if (thaiDateStr == "-") return null;
            
            // If contains time (space), take only date part
            if (thaiDateStr.Contains(' '))
            {
                thaiDateStr = thaiDateStr.Split(' ')[0];
            }
            
            // Normalize separators (hyphens to slashes)
            thaiDateStr = thaiDateStr.Replace('-', '/');
            
            var parts = thaiDateStr.Split('/');
            if (parts.Length == 3 && 
                int.TryParse(parts[0], out var p1) && 
                int.TryParse(parts[1], out var p2) && 
                int.TryParse(parts[2], out var p3))
            {
                int day = p1;
                int month = p2;
                int year = p3;

                // Handle yyyy/MM/dd format
                if (p1 > 1000)
                {
                    year = p1;
                    month = p2;
                    day = p3;
                }
                // Handle MM/dd/yyyy format (month and day are swapped)
                else if (p2 > 12 && p1 <= 12)
                {
                    day = p2;
                    month = p1;
                    year = p3;
                }

                // Convert Buddhist year to Gregorian if year > 2500
                if (year > 2500) year -= 543;
                
                try 
                {
                    var parsed = new DateTime(year, month, day);
                    return parsed;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] Failed to parse '{thaiDateStr}': {ex.Message}");
                    return null;
                }
            }
            
            Console.WriteLine($"[WARNING] Cannot parse date format: '{thaiDateStr}'");
            return null;
        }

        public async Task<IActionResult> Details(string? fileName, int? month, int? year)
        {
            // New mode: filter by month/year
            if (month.HasValue && year.HasValue)
            {
                var thaiMonths = new[] { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน", 
                                         "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };
                
                var monthDisplay = $"{thaiMonths[month.Value]} {year.Value + 543}";
                
                // Get all orders and filter by ApprovedDate
                var allOrders = await _context.OrderTrackingMasters
                    .Include(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();

                var ordersInMonth = allOrders
                    .Where(o => {
                        var parsed = ParseThaiDate(o.ApprovedDate);
                        return parsed.HasValue && parsed.Value.Year == year.Value && parsed.Value.Month == month.Value;
                    })
                    .OrderBy(o => o.PoNumber)
                    .ToList();

                ViewData["ReportName"] = monthDisplay;
                ViewData["CreatedAt"] = DateTime.Now;
                ViewData["UploadedWeeklyPlansCount"] = ordersInMonth
                    .SelectMany(o => o.MatchedInWeeklyPlans.Select(wpd => wpd.WeeklyPlanId))
                    .Distinct()
                    .Count();
                
                return View(ordersInMonth);
            }

            // Old mode: filter by fileName (Report)
            if (string.IsNullOrEmpty(fileName)) return NotFound();
            
            var report = await _context.Reports
                .Include(r => r.Orders)
                    .ThenInclude(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                .FirstOrDefaultAsync(r => r.ReportName == fileName);
            if (report == null) return NotFound();
            int uploadedPlansCount = await _context.WeeklyPlans
                .Where(wp => wp.ReportId == report.Id)
                .CountAsync();
            var orders = report.Orders
                .OrderBy(o => o.Status == "Pending" ? 0 : 1)
                .ThenBy(o => o.PoNumber)
                .ToList();
            ViewData["ReportName"] = report.ReportName;
            ViewData["CreatedAt"] = report.CreatedAt;
            ViewData["UploadedWeeklyPlansCount"] = uploadedPlansCount;
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
        public async Task<IActionResult> ExportToExcel(string? fileName, string? customName, int? month, int? year)
        {
            List<OrderTrackingMaster> orders;
            string sheetTitle;

            var thaiMonths = new[] { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
                                     "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };

            if (month.HasValue && year.HasValue)
            {
                // Month/Year mode
                sheetTitle = $"{thaiMonths[month.Value]} {year.Value + 543}";

                var allOrders = await _context.OrderTrackingMasters
                    .Include(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();

                orders = allOrders
                    .Where(o => {
                        var parsed = ParseThaiDate(o.ApprovedDate);
                        return parsed.HasValue && parsed.Value.Year == year.Value && parsed.Value.Month == month.Value;
                    })
                    .OrderBy(o => o.PoNumber)
                    .ToList();
            }
            else if (!string.IsNullOrEmpty(fileName))
            {
                // FileName mode (legacy)
                var report = await _context.Reports
                    .Include(r => r.Orders)
                        .ThenInclude(o => o.MatchedInWeeklyPlans)
                            .ThenInclude(wpd => wpd.WeeklyPlan)
                    .FirstOrDefaultAsync(r => r.ReportName == fileName);
                if (report == null) return NotFound();

                orders = report.Orders
                    .OrderBy(o => o.Status == "Pending" ? 0 : 1)
                    .ThenBy(o => o.PoNumber)
                    .ToList();
                sheetTitle = fileName;
            }
            else
            {
                return NotFound();
            }

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("รายการสั่งผลิต");

            // ── Font & style constants ───────────────────────────────────────
            var fontName    = "TH SarabunPSK";     // ฟอนต์ไทย/อังกฤษมาตรฐาน
            var colorHeader = XLColor.FromHtml("#1E3A5F");   // navy
            var colorSubH   = XLColor.FromHtml("#2563EB");   // blue-600
            var colorAlt    = XLColor.FromHtml("#F0F4FA");   // สีแถวคู่
            var colorBorder = XLColor.FromHtml("#CBD5E1");   // slate-300
            var colorMatched = XLColor.FromHtml("#D1FAE5");  // emerald-100
            var colorPending = XLColor.FromHtml("#FEE2E2");  // red-100
            var colorTextDark = XLColor.FromHtml("#1E293B");
            var colorTextGray = XLColor.FromHtml("#64748B");
            var colorGreen   = XLColor.FromHtml("#065F46");
            var colorRed     = XLColor.FromHtml("#991B1B");

            int totalRows = orders.Count;
            int matchedCount = orders.Count(o => o.Status == "Matched");
            int pendingCount = totalRows - matchedCount;
            decimal totalAmount = orders.Sum(o => decimal.TryParse(o.Amount, out var a) ? a : 0m);

            // ── Row 1: Title ─────────────────────────────────────────────────
            ws.Cell(1, 1).Value = $"รายงานใบสั่งผลิต — {sheetTitle}";
            var titleCell = ws.Cell(1, 1);
            titleCell.Style.Font.FontName = fontName;
            titleCell.Style.Font.FontSize = 16;
            titleCell.Style.Font.Bold = true;
            titleCell.Style.Font.FontColor = colorHeader;
            ws.Range(1, 1, 1, 10).Merge();

            // ── Row 2: Sub-info ──────────────────────────────────────────────
            ws.Cell(2, 1).Value = $"วันที่ส่งออก: {DateTime.Now:dd/MM/yyyy HH:mm}    " +
                                   $"รายการทั้งหมด: {totalRows}    " +
                                   $"ได้แผนผลิตแล้ว: {matchedCount}    " +
                                   $"ยังไม่มีแผนผลิต: {pendingCount}    " +
                                   $"ยอดรวม: {totalAmount:N2} บาท";
            ws.Cell(2, 1).Style.Font.FontName = fontName;
            ws.Cell(2, 1).Style.Font.FontSize = 11;
            ws.Cell(2, 1).Style.Font.FontColor = colorTextGray;
            ws.Range(2, 1, 2, 10).Merge();

            // ── Row 3: blank spacer ──────────────────────────────────────────
            ws.Row(3).Height = 6;

            // ── Row 4: Column Headers ────────────────────────────────────────
            int headerRow = 4;
            var headers = new[]
            {
                "ลำดับ", "สถานะ", "เลขที่อนุมัติ (PO)", "วันที่สั่ง",
                "วันที่อนุมัติ", "ปภ.ความเร่งด่วน", "จำนวนเงิน (บาท)",
                "รายละเอียด / หมายเหตุ", "จำนวน (ชิ้น)", "เป้าหมายส่งมอบ"
            };

            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.FontName  = fontName;
                cell.Style.Font.FontSize  = 12;
                cell.Style.Font.Bold      = true;
                cell.Style.Font.FontColor = colorHeader;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical   = XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder  = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = colorBorder;
            }
            ws.Row(headerRow).Height = 22;

            // ── Data Rows ────────────────────────────────────────────────────
            for (int i = 0; i < orders.Count; i++)
            {
                var order = orders[i];
                int row = headerRow + 1 + i;
                bool isMatched = order.Status == "Matched";

                var deliveryTarget = order.MatchedInWeeklyPlans.Any()
                    ? order.MatchedInWeeklyPlans
                        .OrderByDescending(wpd => wpd.WeeklyPlan.UploadedAt)
                        .First().DeliveryTarget ?? "-"
                    : "-";

                var proposedPrice = order.MatchedInWeeklyPlans.Any()
                    ? order.MatchedInWeeklyPlans
                        .OrderByDescending(wpd => wpd.WeeklyPlan.UploadedAt)
                        .First().Price ?? order.Amount ?? "-"
                    : order.Amount ?? "-";

                decimal priceVal = 0m;
                bool hasPriceVal = decimal.TryParse(
                    (proposedPrice ?? "").Replace("฿", "").Replace(",", "").Trim(),
                    out priceVal);

                var values = new object?[]
                {
                    i + 1,
                    isMatched ? "ได้แผนผลิตแล้ว" : "ยังไม่มีแผนผลิต",
                    order.PoNumber,
                    order.RequestDate ?? "-",
                    order.ApprovedDate ?? "-",
                    order.Urgency ?? "-",
                    hasPriceVal ? (object)priceVal : (proposedPrice ?? "-"),
                    order.Remarks ?? "-",
                    order.RemarksQuantity ?? "-",
                    deliveryTarget
                };

                for (int c = 0; c < values.Length; c++)
                {
                    var cell = ws.Cell(row, c + 1);
                    cell.Value = values[c] switch
                    {
                        int    iv => XLCellValue.FromObject(iv),
                        decimal dv => XLCellValue.FromObject(dv),
                        string sv => XLCellValue.FromObject(sv),
                        _          => XLCellValue.FromObject(values[c]?.ToString() ?? "")
                    };
                    cell.Style.Font.FontName  = fontName;
                    cell.Style.Font.FontSize  = 11;
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = colorBorder;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    cell.Style.Alignment.WrapText = true;

                    // ปรับสี text สถานะ
                    if (c == 1)
                    {
                        cell.Style.Font.Bold      = true;
                        cell.Style.Font.FontColor = isMatched ? colorGreen : colorRed;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    // จัด center: ลำดับ, PO, วันที่, ปภ, จำนวน, เป้าหมาย
                    else if (c == 0 || c == 2 || c == 3 || c == 4 || c == 5 || c == 8 || c == 9)
                    {
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    // จัด right: ราคา
                    else if (c == 6 && hasPriceVal)
                    {
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        cell.Style.NumberFormat.Format  = "#,##0.00";
                    }
                }

                ws.Row(row).Height = 18;
            }

            // ── Summary row ──────────────────────────────────────────────────
            int sumRow = headerRow + 1 + orders.Count + 1;
            ws.Cell(sumRow, 6).Value = "รวมทั้งหมด";
            ws.Cell(sumRow, 6).Style.Font.FontName  = fontName;
            ws.Cell(sumRow, 6).Style.Font.Bold      = true;
            ws.Cell(sumRow, 6).Style.Font.FontColor = colorHeader;
            ws.Cell(sumRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            ws.Cell(sumRow, 7).Value = totalAmount;
            ws.Cell(sumRow, 7).Style.Font.FontName  = fontName;
            ws.Cell(sumRow, 7).Style.Font.Bold      = true;
            ws.Cell(sumRow, 7).Style.Font.FontColor = colorHeader;
            ws.Cell(sumRow, 7).Style.NumberFormat.Format  = "#,##0.00";
            ws.Cell(sumRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Range(sumRow, 6, sumRow, 7).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range(sumRow, 6, sumRow, 7).Style.Border.OutsideBorderColor = colorBorder;

            // ── Column widths ────────────────────────────────────────────────
            ws.Column(1).Width  = 7;   // ลำดับ
            ws.Column(2).Width  = 20;  // สถานะ
            ws.Column(3).Width  = 18;  // PO
            ws.Column(4).Width  = 14;  // วันที่สั่ง
            ws.Column(5).Width  = 14;  // วันที่อนุมัติ
            ws.Column(6).Width  = 16;  // ปภ
            ws.Column(7).Width  = 16;  // จำนวนเงิน
            ws.Column(8).Width  = 42;  // รายละเอียด
            ws.Column(9).Width  = 12;  // จำนวน
            ws.Column(10).Width = 18;  // เป้าหมาย

            // ── Freeze panes ─────────────────────────────────────────────────
            ws.SheetView.FreezeRows(headerRow);

            // ── Auto-filter ──────────────────────────────────────────────────
            ws.RangeUsed()?.SetAutoFilter();

            // ── Print settings ───────────────────────────────────────────────
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.PaperSize       = XLPaperSize.A4Paper;
            ws.PageSetup.FitToPages(1, 0);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            string downloadName = !string.IsNullOrWhiteSpace(customName)
                ? $"{customName.Trim()}.xlsx"
                : $"Report_{sheetTitle}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

            return File(stream.ToArray(),
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        downloadName);
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
                // ดึงรายการทั้งหมดที่มี PO เดียวกันในคลัง WeeklyPlanDetails เพื่อมาทำประวัติการแก้ไข/ส่งมอบ
                var samePoDetails = await _context.WeeklyPlanDetails
                    .Include(d => d.WeeklyPlan)
                    .Where(d => d.PoNumberInFile == wpdDetail.PoNumberInFile)
                    .OrderByDescending(d => d.WeeklyPlan.UploadedAt)
                    .ToListAsync();

                var history = samePoDetails.Select(d => new
                {
                    fileName = d.WeeklyPlan?.FileName ?? "-",
                    sheetName = d.WeeklyPlan?.SheetName ?? "-",
                    department = d.Department ?? "-",
                    orderName = d.OrderName ?? "-",
                    orderStatus = d.OrderStatus ?? "-",
                    deliveryTarget = d.DeliveryTarget ?? "-",
                    price = d.Price ?? "-",
                    uploadedAt = d.WeeklyPlan != null ? d.WeeklyPlan.UploadedAt.ToString("dd/MM/yyyy HH:mm") : "-"
                }).ToList();
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

            // Parse month/year from reportName if it's in Thai format (e.g., "พฤษภาคม 2569")
            int? filterMonth = null;
            int? filterYear = null;
            if (!string.IsNullOrEmpty(reportName))
            {
                var parts = reportName.Split(' ');
                if (parts.Length == 2)
                {
                    var thaiMonths = new Dictionary<string, int>
                    {
                        {"มกราคม", 1}, {"กุมภาพันธ์", 2}, {"มีนาคม", 3}, {"เมษายน", 4},
                        {"พฤษภาคม", 5}, {"มิถุนายน", 6}, {"กรกฎาคม", 7}, {"สิงหาคม", 8},
                        {"กันยายน", 9}, {"ตุลาคม", 10}, {"พฤศจิกายน", 11}, {"ธันวาคม", 12}
                    };
                    
                    if (thaiMonths.TryGetValue(parts[0], out var month) && 
                        int.TryParse(parts[1], out var buddhistYear))
                    {
                        filterMonth = month;
                        filterYear = buddhistYear - 543; // Convert to Gregorian year
                    }
                }
            }

            var allItems = new List<dynamic>();

            // 1. Query Master Orders (from สั่งผลิต files)
            if (statusFilter != "Unmatched")
            {
                var masterQuery = _context.OrderTrackingMasters
                    .Include(o => o.Report)
                    .Include(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                    .AsQueryable();

                // Filter by month/year if specified
                if (filterMonth.HasValue && filterYear.HasValue)
                {
                    var allMasters = await masterQuery.ToListAsync();
                    var filteredMasters = allMasters
                        .Where(o => {
                            var parsed = ParseThaiDate(o.ApprovedDate);
                            return parsed.HasValue && 
                                   parsed.Value.Year == filterYear.Value && 
                                   parsed.Value.Month == filterMonth.Value;
                        })
                        .ToList();
                    masterQuery = filteredMasters.AsQueryable();
                }

                if (!string.IsNullOrEmpty(search))
                {
                    var s = search.Trim().ToLower();
                    masterQuery = masterQuery.Where(o => 
                        (o.PoNumber ?? "").ToLower().Contains(s) || 
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

            // 2. Query ALL Weekly Plan Details (both Matched and Unmatched)
            if (statusFilter == "All" || statusFilter == "Unmatched")
            {
                var weeklyQuery = _context.WeeklyPlanDetails
                    .Include(d => d.WeeklyPlan)
                        .ThenInclude(wp => wp.Report)
                    .AsQueryable();

                // Show only Unmatched if filter is "Unmatched"
                if (statusFilter == "Unmatched")
                {
                    weeklyQuery = weeklyQuery.Where(d => d.IsMatched == false || d.MatchedOrderId == null);
                }

                // Filter by month/year based on DeliveryTarget
                if (filterMonth.HasValue && filterYear.HasValue)
                {
                    var allWeekly = await weeklyQuery.ToListAsync();
                    var filteredWeekly = allWeekly
                        .Where(d => {
                            var parsed = ParseThaiDate(d.DeliveryTarget);
                            return parsed.HasValue && 
                                   parsed.Value.Year == filterYear.Value && 
                                   parsed.Value.Month == filterMonth.Value;
                        })
                        .ToList();
                    weeklyQuery = filteredWeekly.AsQueryable();
                }

                if (!string.IsNullOrEmpty(search))
                {
                    var s = search.Trim().ToLower();
                    weeklyQuery = weeklyQuery.Where(d => 
                        (d.PoNumberInFile ?? "").ToLower().Contains(s) || 
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
                        Status = d.IsMatched ? "Matched" : "Unmatched",
                        ReportName = d.WeeklyPlan.Report.ReportName ?? "-",
                        WeeklyPlanFile = d.WeeklyPlan.FileName ?? "-",
                        SortDate = d.WeeklyPlan.UploadedAt
                    })
                    .ToListAsync();

                var mappedWeekly = weeklyList.Select(d => new
                {
                    Id = d.Id,
                    PoNumber = d.PoNumber,
                    RequestDate = d.RequestDate,
                    ApprovedDate = d.ApprovedDate,
                    Urgency = d.Urgency,
                    Amount = d.Amount,
                    ProposedPrice = d.ProposedPrice,
                    Remarks = d.Remarks,
                    Status = d.Status,
                    ReportName = d.ReportName,
                    WeeklyPlanFiles = new List<string> { d.WeeklyPlanFile },
                    SortDate = d.SortDate
                });

                allItems.AddRange(mappedWeekly);
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
        public async Task<IActionResult> GetUploadedWeeklyPlanEntries(
            int page = 1, 
            int pageSize = 15, 
            string? search = null, 
            string? reportName = null, 
            string? statusFilter = "All",
            int? month = null,
            int? year = null,
            string? filterMode = "all")  // เปลี่ยน default เป็น "all"
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 15;

            var allItems = new List<dynamic>();

            // ดึงข้อมูลจาก WeeklyPlanDetails ทั้งหมด (ทั้ง Matched และ Unmatched)
            var allWeeklyPlanDetails = await _context.WeeklyPlanDetails
                .Include(d => d.WeeklyPlan)
                    .ThenInclude(wp => wp.Report)
                .Include(d => d.MatchedOrder)  // Include ข้อมูล OrderTrackingMaster ที่ match ไว้
                .ToListAsync();

            // กรองตาม filterMode
            if (filterMode == "upload" && month.HasValue && year.HasValue)
            {
                // กรองเฉพาะ WeeklyPlan ที่เชื่อมกับ Report ที่มีใบสั่งผลิตในเดือน/ปีที่ระบุ
                var targetReportIds = await _context.OrderTrackingMasters
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();
                
                var reportIdsInMonth = targetReportIds
                    .Where(o => {
                        var parsed = ParseThaiDate(o.ApprovedDate);
                        return parsed.HasValue && parsed.Value.Year == year.Value && parsed.Value.Month == month.Value;
                    })
                    .Select(o => o.ReportId)
                    .Distinct()
                    .ToList();

                allWeeklyPlanDetails = allWeeklyPlanDetails
                    .Where(d => reportIdsInMonth.Contains(d.WeeklyPlan.ReportId))
                    .ToList();
            }
            else if (filterMode == "year" && year.HasValue)
            {
                // กรองเฉพาะ WeeklyPlan ที่เชื่อมกับ Report ที่มีใบสั่งผลิตในปีที่ระบุ
                var targetReportIds = await _context.OrderTrackingMasters
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();
                
                var reportIdsInYear = targetReportIds
                    .Where(o => {
                        var parsed = ParseThaiDate(o.ApprovedDate);
                        return parsed.HasValue && parsed.Value.Year == year.Value;
                    })
                    .Select(o => o.ReportId)
                    .Distinct()
                    .ToList();

                allWeeklyPlanDetails = allWeeklyPlanDetails
                    .Where(d => reportIdsInYear.Contains(d.WeeklyPlan.ReportId))
                    .ToList();
            }
            // ถ้า filterMode == "all" ไม่กรองอะไร แสดงทั้งหมดทุกปี

            // จัดกลุ่มตามเลขที่ PO (PoNumberInFile)
            var groupedDetails = allWeeklyPlanDetails
                .GroupBy(d => d.PoNumberInFile?.Trim().ToUpper() ?? "")
                .ToList();

            foreach (var group in groupedDetails)
            {
                var po = group.Key;
                if (string.IsNullOrEmpty(po)) continue;

                // เรียงลำดับรายการในกลุ่มจากล่าสุดไปเก่าสุดตาม UploadedAt ของไฟล์แผนผลิต
                var sortedGroup = group
                    .OrderByDescending(d => d.WeeklyPlan.UploadedAt)
                    .ToList();

                var latestDetail = sortedGroup.First();

                // ตรวจสอบสถานะ
                string status = "Unmatched"; // ค่าเริ่มต้น
                string approvedDate = "-";
                string urgency = "-";
                Guid? matchedOrderId = null;

                // เช็คว่ามีรายการใดในกลุ่มนี้ที่จับคู่สำเร็จบ้าง (ถ้ามี ให้ใช้ข้อมูลของตัวที่จับคู่ได้)
                var matchedDetail = sortedGroup.FirstOrDefault(d => d.IsMatched && d.MatchedOrderId.HasValue && d.MatchedOrder != null);
                if (matchedDetail != null)
                {
                    status = matchedDetail.MatchedOrder!.Status ?? "Matched";
                    approvedDate = matchedDetail.MatchedOrder.ApprovedDate ?? "-";
                    urgency = matchedDetail.MatchedOrder.Urgency ?? "-";
                    matchedOrderId = matchedDetail.MatchedOrderId;
                }

                // รวบรวมรายชื่อไฟล์แผนผลิตทั้งหมดในกลุ่มนี้แบบไม่ซ้ำ
                var fileNames = sortedGroup
                    .Select(d => d.WeeklyPlan.FileName ?? "-")
                    .Distinct()
                    .ToArray();

                allItems.Add(new
                {
                    // ID สำหรับเปิด Modal
                    id = matchedOrderId ?? latestDetail.Id,
                    poNumber = latestDetail.PoNumberInFile ?? "-",
                    approvedDate = approvedDate,
                    urgency = urgency,
                    amount = latestDetail.Price ?? "-",
                    remarks = latestDetail.OrderName ?? "-",
                    status = status,
                    weeklyPlanFiles = fileNames,
                    sortDate = latestDetail.WeeklyPlan.UploadedAt,
                    weeklyPlanId = latestDetail.WeeklyPlanId
                });
            }

            // จัดกลุ่มเรียบร้อยแล้ว
            // คัดแยกประวัติไว้เปิดดูในหน้าต่างรายละเอียด (OrderDetailModal) แทนการแสดงซ้ำหลายแถว

            // Apply search filter
            var filteredItems = allItems.AsEnumerable();
            if (!string.IsNullOrEmpty(search))
            {
                var s = search.Trim().ToLower();
                filteredItems = filteredItems.Where(item => 
                    ((string)item.poNumber).ToLower().Contains(s) || 
                    ((string)item.remarks).ToLower().Contains(s));
            }

            // Apply status filter
            if (statusFilter == "Matched")
            {
                filteredItems = filteredItems.Where(item => (string)item.status != "Unmatched");
            }
            else if (statusFilter == "Unmatched")
            {
                filteredItems = filteredItems.Where(item => (string)item.status == "Unmatched");
            }
            // statusFilter == "All" แสดงทั้งหมด (ไม่ต้องกรอง)

            // Sort and paginate
            var sortedItems = filteredItems
                .OrderByDescending(item => (DateTime)item.sortDate)
                .ThenBy(item => (string)item.poNumber)
                .ToList();

            var totalCount = sortedItems.Count;
            var items = sortedItems
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Json(new { success = true, totalCount, page, pageSize, items });
        }

        [HttpGet] 
        public IActionResult AllOrders(string? returnReportName, int? month, int? year)
        {
            ViewBag.ReturnReportName = returnReportName;
            ViewBag.FilterMonth = month;
            ViewBag.FilterYear = year;
            return View();
        }
    }
}
