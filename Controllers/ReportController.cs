using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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

        // GET: /Report
        public async Task<IActionResult> Index()
        {
            var query = _context.ImportSessions.AsQueryable();
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

            if (!isAdmin && !string.IsNullOrEmpty(currentUserId))
            {
                query = query.Where(s => s.UserId == currentUserId);
            }

            var sessions = await query
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var reportList = new System.Collections.Generic.List<ReportSummaryViewModel>();

            foreach (var session in sessions)
            {
                reportList.Add(new ReportSummaryViewModel
                {
                    SessionId = session.Id,
                    ReportName = session.SourceFileName,
                    CompareFileName = session.CompareFileName,
                    CreatedAt = session.CreatedAt,
                    TotalRows = session.MatchedCount + session.UnmatchedCount,
                    MatchedRows = session.MatchedCount
                });
            }

            return View(reportList);
        }

        // GET: /Report/Details
        public async Task<IActionResult> Details(Guid sessionId)
        {
            var session = await _context.ImportSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
            {
                return NotFound("ไม่พบรายงานที่ระบุ");
            }

            // User Isolation Check
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            if (!isAdmin && session.UserId != currentUserId)
            {
                return Forbid();
            }

            var rows = await _context.MergeResults
                .Where(p => p.ImportSessionId == sessionId)
                .ToListAsync();

            ViewData["ReportName"] = session.SourceFileName + " & " + session.CompareFileName;
            ViewData["SessionId"] = sessionId;
            ViewData["CreatedAt"] = session.CreatedAt;

            return View(rows);
        }

        // GET: /Report/ExportReport
        public async Task<IActionResult> ExportReport(Guid sessionId)
        {
            var session = await _context.ImportSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
            {
                return NotFound("ไม่พบรายงานที่ระบุ");
            }

            // User Isolation Check
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            if (!isAdmin && session.UserId != currentUserId)
            {
                return Forbid();
            }

            var rows = await _context.MergeResults
                .Where(p => p.ImportSessionId == sessionId)
                .ToListAsync();

            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("รายงานเปรียบเทียบ");

                // Set Font Name
                worksheet.Style.Font.FontName = "Segoe UI";

                // Title Block
                worksheet.Cell(1, 1).Value = "รายงานการเปรียบเทียบข้อมูลสั่งผลิตชิ้นส่วน";
                worksheet.Cell(1, 1).Style.Font.Bold = true;
                worksheet.Cell(1, 1).Style.Font.FontSize = 15;
                worksheet.Cell(1, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#1E293B");

                worksheet.Cell(2, 1).Value = $"ชื่อรายงาน: {session.SourceFileName}";
                worksheet.Cell(2, 1).Style.Font.FontSize = 10;
                worksheet.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#475569");

                worksheet.Cell(3, 1).Value = $"วันที่สร้างรายงาน: {session.CreatedAt.ToString("dd MMMM yyyy เวลา HH:mm น.", new System.Globalization.CultureInfo("th-TH"))}";
                worksheet.Cell(3, 1).Style.Font.FontSize = 10;
                worksheet.Cell(3, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#475569");

                // Headers
                string[] headers = {
                    "ลำดับ", "ใบขออนุมัติ(สั่งผลิต)", "เลขที่อนุมัติ(แผนผลิต)", "วันที่อนุมัติขอสั่งผลิต", 
                    "ปภ.ความเร่งด่วน", "จำนวนชิ้น", "มูลค่าสั่งผลิต", "หมายเหตุเพิ่มเติม", "เป้าหมายส่งมอบ", "สถานะการจับคู่"
                };

                int startRow = 5;
                worksheet.Row(startRow).Height = 26;

                for (int c = 0; c < headers.Length; c++)
                {
                    var cell = worksheet.Cell(startRow, c + 1);
                    cell.Value = headers[c];
                    cell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                }

                // Styling headers
                var headerRange = worksheet.Range(startRow, 1, startRow, headers.Length);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontSize = 11;
                headerRange.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#1E3A8A"); // Navy blue
                headerRange.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;

                int row = startRow + 1;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    worksheet.Row(row).Height = 20;

                    // Values
                    worksheet.Cell(row, 1).Value = i + 1;
                    worksheet.Cell(row, 2).Value = !string.IsNullOrEmpty(r.PoNumber) ? r.PoNumber : "-";
                    worksheet.Cell(row, 3).Value = !string.IsNullOrEmpty(r.PlanOrderNo) ? r.PlanOrderNo : "-";
                    worksheet.Cell(row, 4).Value = r.ApprovedDate.HasValue ? r.ApprovedDate.Value.ToString("dd/MM/yyyy") : "-";
                    worksheet.Cell(row, 5).Value = r.Urgency ?? "-";
                    worksheet.Cell(row, 6).Value = r.Quantity.HasValue ? r.Quantity.Value : 0;
                    worksheet.Cell(row, 7).Value = r.Amount.HasValue ? r.Amount.Value : 0;
                    worksheet.Cell(row, 8).Value = r.Remarks ?? "-";
                    worksheet.Cell(row, 9).Value = !string.IsNullOrEmpty(r.DeliveryTargetDate) ? r.DeliveryTargetDate : "-";
                    
                    var statusCell = worksheet.Cell(row, 10);
                    statusCell.Value = r.IsMatched ? "ตรงตามแผนผลิต" : "ไม่พบการสั่งซื้อ";

                    // Alignments
                    worksheet.Cell(row, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 2).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 3).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(row, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(row, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
                    worksheet.Cell(row, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    statusCell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                    // Numeric formatting
                    worksheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
                    worksheet.Cell(row, 7).Style.NumberFormat.Format = "฿#,##0.00";

                    // Conditional Styling for Status
                    if (r.IsMatched)
                    {
                        statusCell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#D1FAE5"); // Light green
                        statusCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#065F46"); // Dark green
                        statusCell.Style.Font.Bold = true;
                    }
                    else
                    {
                        statusCell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#FFE4E6"); // Light red
                        statusCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#991B1B"); // Dark red
                        statusCell.Style.Font.Bold = true;
                    }

                    // Zebra striping for even rows
                    if (i % 2 == 1)
                    {
                        for (int c = 1; c < headers.Length; c++) // skip status column which has conditional styling
                        {
                            worksheet.Cell(row, c).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#F8FAFC"); // Slate-50
                        }
                    }

                    row++;
                }

                // Add gridlines & borders
                var tableRange = worksheet.Range(startRow, 1, row - 1, headers.Length);
                tableRange.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                tableRange.Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("#CBD5E1");
                tableRange.Style.Border.InsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                tableRange.Style.Border.InsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("#E2E8F0");

                worksheet.Columns().AdjustToContents();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    string safeFileName = string.Join("_", session.SourceFileName.Split(Path.GetInvalidFileNameChars()));
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"MergedReport_{safeFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                }
            }
        }

        // POST: /Report/DeleteSession
        [HttpPost]
        public async Task<IActionResult> DeleteSession(Guid sessionId)
        {
            var session = await _context.ImportSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
            {
                return Json(new { success = false, error = "ไม่พบรายงานที่ระบุ" });
            }

            // Delete associated merge results
            var results = _context.MergeResults.Where(r => r.ImportSessionId == sessionId);
            _context.MergeResults.RemoveRange(results);

            _context.ImportSessions.Remove(session);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
    }

    public class ReportSummaryViewModel
    {
        public Guid SessionId { get; set; }
        public string ReportName { get; set; } = string.Empty;
        public string CompareFileName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int TotalRows { get; set; }
        public int MatchedRows { get; set; }
    }
}
