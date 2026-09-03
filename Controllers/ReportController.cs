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
    [Authorize(Roles = "Admin,Dev")]
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
            // 2. ถ้าไม่มี ให้ใช้ปีปัจจุบันของเครื่องเป็นค่าเริ่มต้น (Default)
            var selectedYear = year ?? now.Year;

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

            // หาเดือนล่าสุดที่มีข้อมูลในปีที่เลือก (Latest month with data)
            int latestMonthWithData = ordersByMonth.Keys.Any() ? ordersByMonth.Keys.Max() : 0;

            // Build 12 month cards
            var cards = new List<object>();

            int runningTotalOrders = 0;
            decimal runningTotalAmount = 0m;

            for (int month = 1; month <= 12; month++)
            {
                var monthKey = $"{selectedYear:0000}-{month:00}";
                var monthDisplay = $"{thaiMonths[month]} {selectedYear + 543}";
                
                var ordersInMonth = ordersByMonth.ContainsKey(month) ? ordersByMonth[month] : [];
                var monthOrdersCount = ordersInMonth.Count;
                var monthAmount = 0m;
                
                foreach (var order in ordersInMonth)
                {
                    monthAmount += ParseDecimal(order.Amount);
                }

                runningTotalOrders += monthOrdersCount;
                runningTotalAmount += monthAmount;

                var isCurrentMonth = (selectedYear == now.Year && month == now.Month);
                var isEmpty = (monthOrdersCount == 0);
                var isLatestWithData = (month == latestMonthWithData && !isEmpty);

                var statusCode = isEmpty ? "empty" : (isCurrentMonth ? "active" : "completed");
                var statusLabel = isEmpty ? "ไม่มีข้อมูล" : (isCurrentMonth ? "กำลังดำเนินการ" : "มีข้อมูล");

                cards.Add(new {
                    MonthYearDisplay = monthDisplay,
                    MonthYearKey = monthKey,
                    Month = month,
                    Year = selectedYear,
                    MonthOrders = monthOrdersCount,
                    MonthAmount = monthAmount,
                    TotalOrders = isEmpty ? 0 : runningTotalOrders,
                    TotalAmount = isEmpty ? 0m : runningTotalAmount,
                    IsLatestWithData = isLatestWithData,
                    StatusCode = statusCode,
                    StatusLabel = statusLabel
                });
            }

            ViewData["SelectedYear"] = selectedYear;
            ViewData["CurrentYear"] = now.Year;

            // กำหนดช่วงปีสำหรับให้เลือกใน Dropdown:
            // - แสดงเฉพาะปีที่มีการ์ด/ข้อมูลในระบบจริง
            // - รวมปีปัจจุบัน (CurrentYear) และปีที่เลือก (SelectedYear) เสมอ
            var availableYears = parsedYears
                .Concat(new[] { now.Year, selectedYear })
                .Distinct()
                .OrderByDescending(yr => yr)
                .ToList();
            ViewData["AvailableYears"] = availableYears;

            ViewData["ExistingReports"] = await _context.Reports
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => r.ReportName)
                .ToListAsync();
            
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
                        return parsed.HasValue && parsed.Value.Year == year.Value;
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
        public async Task<IActionResult> ExportToExcel(string? fileName, string? customName, int? month, int? year, string? status = null, string? search = null, string? sortBy = null, string? sortOrder = null)
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

                orders = report.Orders.ToList();
                sheetTitle = fileName;
            }
            else
            {
                return NotFound();
            }

            // Apply Status Filter
            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (status.Equals("matched", StringComparison.OrdinalIgnoreCase))
                    orders = orders.Where(o => o.Status == "Matched").ToList();
                else if (status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                    orders = orders.Where(o => o.Status != "Matched").ToList();
            }

            // Apply Search Filter
            if (!string.IsNullOrWhiteSpace(search))
            {
                var keywords = search.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (keywords.Length > 0)
                {
                    orders = orders.Where(o => keywords.Any(kw =>
                        (o.PoNumber != null && o.PoNumber.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                        (o.Remarks != null && o.Remarks.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                        (o.Urgency != null && o.Urgency.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    )).ToList();
                }
            }

            // Apply Column Sorting
            if (!string.IsNullOrWhiteSpace(sortBy))
            {
                bool isDesc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
                switch (sortBy.ToLower())
                {
                    case "po":
                        orders = isDesc ? orders.OrderByDescending(o => o.PoNumber).ToList() : orders.OrderBy(o => o.PoNumber).ToList();
                        break;
                    case "approved-date":
                        orders = isDesc
                            ? orders.OrderByDescending(o => ParseThaiDate(o.ApprovedDate) ?? DateTime.MinValue).ThenByDescending(o => o.PoNumber).ToList()
                            : orders.OrderBy(o => ParseThaiDate(o.ApprovedDate) ?? DateTime.MaxValue).ThenBy(o => o.PoNumber).ToList();
                        break;
                    case "price":
                        orders = isDesc
                            ? orders.OrderByDescending(o => {
                                var p = o.MatchedInWeeklyPlans?.OrderByDescending(w => w.WeeklyPlan?.UploadedAt).FirstOrDefault()?.Price ?? o.Amount;
                                return decimal.TryParse((p ?? "").Replace("฿", "").Replace(",", "").Trim(), out var val) ? val : 0m;
                            }).ThenBy(o => o.PoNumber).ToList()
                            : orders.OrderBy(o => {
                                var p = o.MatchedInWeeklyPlans?.OrderByDescending(w => w.WeeklyPlan?.UploadedAt).FirstOrDefault()?.Price ?? o.Amount;
                                return decimal.TryParse((p ?? "").Replace("฿", "").Replace(",", "").Trim(), out var val) ? val : 0m;
                            }).ThenBy(o => o.PoNumber).ToList();
                        break;
                    case "quantity":
                        orders = isDesc
                            ? orders.OrderByDescending(o => int.TryParse(o.RemarksQuantity, out var q) ? q : 0).ThenBy(o => o.PoNumber).ToList()
                            : orders.OrderBy(o => int.TryParse(o.RemarksQuantity, out var q) ? q : 0).ThenBy(o => o.PoNumber).ToList();
                        break;
                    default:
                        orders = orders.OrderBy(o => o.PoNumber).ToList();
                        break;
                }
            }
            else
            {
                // Standard default sort: PO Number
                orders = orders.OrderBy(o => o.PoNumber).ToList();
            }

            // ClosedXML Graphic Engine
            ClosedXML.Excel.LoadOptions.DefaultGraphicEngine = new MockGraphicEngine();
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("รายการสั่งผลิต");
            ws.ShowGridLines = true;

            // ── Font & style constants ───────────────────────────────────────
            var fontName = "Noto Sans Thai";
            var colorBorder = XLColor.FromHtml("#E2E8F0");
            var colorBorderDark = XLColor.FromHtml("#CBD5E1");

            int totalRows = orders.Count;
            int matchedCount = orders.Count(o => o.Status == "Matched");
            int pendingCount = totalRows - matchedCount;
            decimal totalAmount = orders.Sum(o => decimal.TryParse(o.Amount, out var a) ? a : 0m);

            // ── 1. Title Banner (Row 1) ──────────────────────────────────────
            ws.Row(1).Height = 32;
            var titleCell = ws.Cell(1, 1);
            titleCell.Value = $"  รายงานใบสั่งผลิต — {sheetTitle}";
            titleCell.Style.Font.FontName = fontName;
            titleCell.Style.Font.FontSize = 13.5;
            titleCell.Style.Font.Bold = true;
            titleCell.Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            titleCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            titleCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            ws.Range(1, 1, 1, 10).Merge();
            ws.Range(1, 1, 1, 10).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range(1, 1, 1, 10).Style.Border.OutsideBorderColor = colorBorder;

            // ── 2. Filter Subtitle (Row 2) ──────────────────────────────────
            ws.Row(2).Height = 24;
            string statusDesc = status switch
            {
                "matched" => "ได้แผนผลิตแล้ว",
                "pending" => "ยังไม่มีแผนผลิต",
                _ => "ทุกสถานะ"
            };
            string searchDesc = !string.IsNullOrWhiteSpace(search) ? $"   |   ค้นหา: \"{search}\"" : "";
            string sortDesc = "";
            if (!string.IsNullOrWhiteSpace(sortBy))
            {
                string colName = sortBy.ToLower() switch
                {
                    "po" => "เลขที่อนุมัติ (PO)",
                    "approved-date" => "วันที่อนุมัติ",
                    "price" => "ราคา",
                    "quantity" => "จำนวน",
                    _ => sortBy
                };
                string orderDir = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase) ? "มากไปน้อย" : "น้อยไปมาก";
                sortDesc = $"   |   เรียงตาม: {colName} ({orderDir})";
            }

            var subCell = ws.Cell(2, 1);
            subCell.Value = $"  งวด: {sheetTitle}   |   สถานะ: {statusDesc}{searchDesc}{sortDesc}   |   ข้อมูลที่ส่งออก: {totalRows} รายการ   |   ยอดรวม: {totalAmount:N2} บาท   |   วันที่ออกรายงาน: {DateTime.Now:dd/MM/yyyy HH:mm}";
            subCell.Style.Font.FontName = fontName;
            subCell.Style.Font.FontSize = 9.5;
            subCell.Style.Font.FontColor = XLColor.FromHtml("#64748B");
            subCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            subCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            ws.Range(2, 1, 2, 10).Merge();
            ws.Range(2, 1, 2, 10).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range(2, 1, 2, 10).Style.Border.OutsideBorderColor = colorBorder;

            // ── Row 3: Blank Spacer ──────────────────────────────────────────
            ws.Row(3).Height = 10;

            // ── 3. Table Header (Row 4) ──────────────────────────────────────
            int headerRow = 4;
            ws.Row(headerRow).Height = 28;
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
                cell.Style.Font.FontName = fontName;
                cell.Style.Font.FontSize = 10.5;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.FromHtml("#1E293B");
                cell.Style.Alignment.Horizontal = c == 7 ? XLAlignmentHorizontalValues.Left :
                                                  c == 6 ? XLAlignmentHorizontalValues.Right :
                                                  XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = colorBorderDark;
                cell.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
                cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#94A3B8");
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            }

            // ── 4. Table Data Rows ──────────────────────────────────────────
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

                string singleLineRemarks = (order.Remarks ?? "-").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();

                var values = new object?[]
                {
                    i + 1,
                    isMatched ? "ได้แผนผลิตแล้ว" : "ยังไม่มีแผนผลิต",
                    order.PoNumber,
                    order.RequestDate ?? "-",
                    order.ApprovedDate ?? "-",
                    order.Urgency ?? "-",
                    hasPriceVal ? (object)priceVal : (proposedPrice ?? "-"),
                    singleLineRemarks,
                    order.RemarksQuantity ?? "-",
                    deliveryTarget
                };

                // Standard uniform row height
                ws.Row(row).Height = 22;

                for (int c = 0; c < values.Length; c++)
                {
                    var cell = ws.Cell(row, c + 1);
                    cell.Value = values[c] switch
                    {
                        int iv => XLCellValue.FromObject(iv),
                        decimal dv => XLCellValue.FromObject(dv),
                        string sv => XLCellValue.FromObject(sv),
                        _ => XLCellValue.FromObject(values[c]?.ToString() ?? "")
                    };
                    cell.Style.Font.FontName = fontName;
                    cell.Style.Font.FontSize = 10;
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = colorBorder;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                    // ปรับสไตล์ Pill สถานะ
                    if (c == 1)
                    {
                        cell.Style.Font.Bold = true;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        if (isMatched)
                        {
                            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D1FAE5");
                            cell.Style.Font.FontColor = XLColor.FromHtml("#065F46");
                        }
                        else
                        {
                            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE4E6");
                            cell.Style.Font.FontColor = XLColor.FromHtml("#9F1239");
                        }
                    }
                    // จัด center: ลำดับ, PO, วันที่, ปภ, จำนวน, เป้าหมาย
                    else if (c == 0 || c == 2 || c == 3 || c == 4 || c == 5 || c == 8 || c == 9)
                    {
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        cell.Style.Font.FontColor = c == 2 ? XLColor.FromHtml("#0F172A") : XLColor.FromHtml("#475569");
                        if (c == 2) cell.Style.Font.Bold = true;
                    }
                    // จัด left: รายละเอียด
                    else if (c == 7)
                    {
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                        cell.Style.Font.FontColor = XLColor.FromHtml("#334155");
                    }
                    // จัด right: ราคา
                    else if (c == 6)
                    {
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                        if (hasPriceVal)
                        {
                            cell.Style.NumberFormat.Format = "#,##0.00";
                            cell.Style.Font.Bold = true;
                            cell.Style.Font.FontColor = XLColor.FromHtml("#0F172A");
                        }
                    }
                }
            }

            // ── 5. Summary Row ──────────────────────────────────────────────
            int sumRow = headerRow + 1 + orders.Count + 1;
            ws.Row(sumRow).Height = 24;

            ws.Cell(sumRow, 6).Value = $"รวมทั้งสิ้น ({totalRows} รายการ)";
            ws.Cell(sumRow, 6).Style.Font.FontName = fontName;
            ws.Cell(sumRow, 6).Style.Font.Bold = true;
            ws.Cell(sumRow, 6).Style.Font.FontSize = 11;
            ws.Cell(sumRow, 6).Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            ws.Cell(sumRow, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(sumRow, 6).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            ws.Cell(sumRow, 7).Value = totalAmount;
            ws.Cell(sumRow, 7).Style.Font.FontName = fontName;
            ws.Cell(sumRow, 7).Style.Font.Bold = true;
            ws.Cell(sumRow, 7).Style.Font.FontSize = 11;
            ws.Cell(sumRow, 7).Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            ws.Cell(sumRow, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(sumRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(sumRow, 7).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            ws.Range(sumRow, 6, sumRow, 7).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            ws.Range(sumRow, 6, sumRow, 7).Style.Border.TopBorderColor = colorBorder;
            ws.Range(sumRow, 6, sumRow, 7).Style.Border.BottomBorder = XLBorderStyleValues.Double;
            ws.Range(sumRow, 6, sumRow, 7).Style.Border.BottomBorderColor = XLColor.Black;

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

            string cleanTitle = sheetTitle.Trim().Replace(" ", "_");
            string downloadName = !string.IsNullOrWhiteSpace(customName)
                ? $"{customName.Trim()}.xlsx"
                : $"รายงานใบสั่งผลิต_{cleanTitle}.xlsx";

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
                // กรองเฉพาะรายการที่อนุมัติในเดือน/ปีที่ระบุ
                var targetOrders = await _context.OrderTrackingMasters
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();
                
                var validOrderIdsInMonth = targetOrders
                    .Where(o => {
                        var parsed = ParseThaiDate(o.ApprovedDate);
                        return parsed.HasValue && parsed.Value.Year == year.Value && parsed.Value.Month == month.Value;
                    })
                    .Select(o => o.Id)
                    .ToHashSet();

                var reportIdsInMonth = targetOrders
                    .Where(o => {
                        var parsed = ParseThaiDate(o.ApprovedDate);
                        return parsed.HasValue && parsed.Value.Year == year.Value && parsed.Value.Month == month.Value;
                    })
                    .Select(o => o.ReportId)
                    .Distinct()
                    .ToHashSet();

                allWeeklyPlanDetails = allWeeklyPlanDetails
                    .Where(d => {
                        // ถ้าจับคู่แล้ว ต้องเป็น PO ที่อนุมัติในเดือนนี้เท่านั้น
                        if (d.IsMatched && d.MatchedOrderId.HasValue)
                        {
                            return validOrderIdsInMonth.Contains(d.MatchedOrderId.Value);
                        }
                        // ถ้ายังไม่ได้จับคู่ ให้ดูตาม ReportId ของเดือนนั้น
                        return reportIdsInMonth.Contains(d.WeeklyPlan.ReportId);
                    })
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
                string? masterAmount = null;

                // เช็คว่ามีรายการใดในกลุ่มนี้ที่จับคู่สำเร็จบ้าง (ถ้ามี ให้ใช้ข้อมูลของตัวที่จับคู่ได้)
                var matchedDetail = sortedGroup.FirstOrDefault(d => d.IsMatched && d.MatchedOrderId.HasValue && d.MatchedOrder != null);
                if (matchedDetail != null)
                {
                    status = matchedDetail.MatchedOrder!.Status ?? "Matched";
                    approvedDate = matchedDetail.MatchedOrder.ApprovedDate ?? "-";
                    urgency = matchedDetail.MatchedOrder.Urgency ?? "-";
                    matchedOrderId = matchedDetail.MatchedOrderId;
                    masterAmount = matchedDetail.MatchedOrder.Amount;
                }

                // กำหนด proposedPrice (ถ้าไม่มีราคาในแผนผลิตสัปดาห์ ให้ใช้ราคาจากใบสั่งผลิตหลัก)
                string proposedPrice = (!string.IsNullOrEmpty(latestDetail.Price) && latestDetail.Price != "-")
                    ? latestDetail.Price
                    : (!string.IsNullOrEmpty(masterAmount) && masterAmount != "-" ? masterAmount : "-");

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
                    proposedPrice = proposedPrice,
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

        private static decimal ParseDecimal(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return 0m;
            val = val.Replace(",", "").Replace("฿", "").Trim();
            return decimal.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var res) ? res : 0m;
        }
    }
}
