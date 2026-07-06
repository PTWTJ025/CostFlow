using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace CostFlow.Controllers
{
    [Authorize]
    public class ProductSearchController : Controller
    {
        private readonly AppDbContext _context;

        public ProductSearchController(AppDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(Guid? editBatchId = null)
        {
            if (editBatchId.HasValue)
            {
                var batch = await _context.SparePartOrderBatches.FirstOrDefaultAsync(b => b.Id == editBatchId.Value);
                if (batch != null)
                {
                    // User Isolation Check
                    bool isAdmin = User.IsInRole("Admin");
                    string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
                    if (!isAdmin && batch.UserId != currentUserId)
                    {
                        return Forbid();
                    }

                    var orders = await _context.SparePartOrders
                        .Where(o => o.BatchId == editBatchId.Value)
                        .OrderBy(o => o.Id)
                        .ToListAsync();
                    
                    ViewData["EditBatchId"] = editBatchId.Value;
                    ViewData["EditBatchName"] = batch.BatchName;
                    ViewData["EditOrdersJson"] = System.Text.Json.JsonSerializer.Serialize(orders.Select(o => new {
                        productCode = o.ProductCode,
                        productName = o.ProductName,
                        unit = o.Unit,
                        unitPrice = (double)o.UnitPrice,
                        quantity = (double)o.Quantity,
                        totalAmount = (double)o.TotalAmount,
                        approvalNo = o.ApprovalNo ?? "",
                        remarks = o.Remarks ?? "",
                        receiveDate = o.ReceiveDate.HasValue ? o.ReceiveDate.Value.ToString("yyyy-MM-dd") : ""
                    }));
                }
            }
            return View();
        }

        // GET: /ProductSearch/Suggest
        [HttpGet]
        public async Task<IActionResult> Suggest(string q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Json(new List<ProductPrice>());
            }

            string cleanQuery = q.Trim().ToLower();

            var matches = await _context.ProductPrices
                .Where(p => p.ProductCode.ToLower().Contains(cleanQuery) || 
                            p.ProductName.ToLower().Contains(cleanQuery))
                .Take(15)
                .ToListAsync();

            return Json(matches);
        }

        // POST: /ProductSearch/SaveOrders
        [HttpPost]
        public async Task<IActionResult> SaveOrders([FromBody] SaveOrdersRequest request)
        {
            Console.WriteLine($"[DEBUG SaveOrders] Request received. Is null? {request == null}");
            if (request != null)
            {
                Console.WriteLine($"[DEBUG SaveOrders] BatchId: '{request.BatchId}', BatchName: '{request.BatchName}'");
                Console.WriteLine($"[DEBUG SaveOrders] Orders is null? {request.Orders == null}");
                if (request.Orders != null)
                {
                    Console.WriteLine($"[DEBUG SaveOrders] Orders count: {request.Orders.Count}");
                    for (int i = 0; i < request.Orders.Count; i++)
                    {
                        var o = request.Orders[i];
                        Console.WriteLine($"  [{i}] Code: '{o.ProductCode}', Name: '{o.ProductName}', Price: '{o.UnitPrice}', Qty: '{o.Quantity}'");
                    }
                }
            }

            if (request == null || request.Orders == null || !request.Orders.Any())
            {
                return Json(new { success = false, error = "ไม่มีข้อมูลใบสั่งซื้อที่จะบันทึก" });
            }

            try
            {
                var now = DateTime.Now;
                Guid batchId = request.BatchId ?? Guid.NewGuid();
                var batchName = string.IsNullOrWhiteSpace(request.BatchName)
                    ? $"รายการคีย์ข้อมูลวันที่ {now.ToString("dd/MM/yyyy HH:mm")}"
                    : request.BatchName.Trim();

                // Get logged in UserId or fallback
                string userId = string.Empty;
                var claimUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!string.IsNullOrEmpty(claimUserId))
                {
                    userId = claimUserId;
                }
                else
                {
                    var defaultUser = _context.Users.FirstOrDefault(u => u.EmployeeCode == "ADMIN01");
                    userId = defaultUser?.Id ?? string.Empty;
                }

                // If editing existing batch, clear old orders first
                var existingBatch = request.BatchId.HasValue 
                    ? await _context.SparePartOrderBatches.FirstOrDefaultAsync(b => b.Id == request.BatchId.Value) 
                    : null;

                if (existingBatch != null)
                {
                    var oldOrders = _context.SparePartOrders.Where(o => o.BatchId == existingBatch.Id);
                    _context.SparePartOrders.RemoveRange(oldOrders);
                }

                decimal totalBatchAmount = 0;
                int itemsCount = 0;

                foreach (var o in request.Orders)
                {
                    decimal price = ParseDecimal(o.UnitPrice);
                    decimal qty = ParseDecimal(o.Quantity);
                    decimal rowTotal = price * qty;

                    var order = new SparePartOrder
                    {
                        BatchId = batchId,
                        ProductCode = o.ProductCode ?? string.Empty,
                        ProductName = o.ProductName ?? string.Empty,
                        Unit = o.Unit,
                        UnitPrice = price,
                        Quantity = qty,
                        TotalAmount = rowTotal,
                        ApprovalNo = o.ApprovalNo,
                        Remarks = o.Remarks,
                        ReceiveDate = ParseDateNullable(o.ReceiveDate),
                        CreatedAt = now
                    };

                    totalBatchAmount += rowTotal;
                    itemsCount++;

                    _context.SparePartOrders.Add(order);
                }

                if (existingBatch != null)
                {
                    // Update existing batch metadata
                    existingBatch.BatchName = batchName;
                    existingBatch.ItemCount = itemsCount;
                    existingBatch.TotalAmount = totalBatchAmount;
                    _context.SparePartOrderBatches.Update(existingBatch);
                }
                else
                {
                    // Create the new batch record
                    var batch = new SparePartOrderBatch
                    {
                        Id = batchId,
                        BatchName = batchName,
                        UserId = userId,
                        ItemCount = itemsCount,
                        TotalAmount = totalBatchAmount,
                        CreatedAt = now
                    };
                    _context.SparePartOrderBatches.Add(batch);
                }

                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาดในการบันทึก: {ex.Message}" });
            }
        }

        // POST: /ProductSearch/DeleteBatch
        [HttpPost]
        public async Task<IActionResult> DeleteBatch(Guid batchId)
        {
            var batch = await _context.SparePartOrderBatches.FirstOrDefaultAsync(b => b.Id == batchId);
            if (batch == null)
            {
                return Json(new { success = false, error = "ไม่พบแผ่นงานสั่งซื้อที่ต้องการลบ" });
            }

            // User Isolation Check
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            if (!isAdmin && batch.UserId != currentUserId)
            {
                return Json(new { success = false, error = "คุณไม่มีสิทธิ์ในการเข้าถึงหรือดำเนินการกับแผ่นงานสั่งซื้อนี้" });
            }

            var orders = _context.SparePartOrders.Where(o => o.BatchId == batchId);
            _context.SparePartOrders.RemoveRange(orders);
            _context.SparePartOrderBatches.Remove(batch);

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // GET: /ProductSearch/SavedOrders
        [HttpGet]
        public async Task<IActionResult> SavedOrders(int? year, int? month)
        {
            var query = _context.SparePartOrderBatches.AsQueryable();

            // User Isolation: If not Admin, show only their own batches
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

            if (!isAdmin && !string.IsNullOrEmpty(currentUserId))
            {
                query = query.Where(o => o.UserId == currentUserId);
            }

            if (year.HasValue && year.Value > 0)
            {
                query = query.Where(o => o.CreatedAt.Year == year.Value);
            }
            if (month.HasValue && month.Value > 0)
            {
                query = query.Where(o => o.CreatedAt.Month == month.Value);
            }

            var batches = await query
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => new SavedBatchViewModel
                {
                    BatchId = b.Id,
                    BatchName = b.BatchName,
                    CreatedAt = b.CreatedAt,
                    TotalItems = b.ItemCount,
                    TotalAmount = (double)b.TotalAmount
                })
                .ToListAsync();

            // Get unique years in db for dropdown
            var years = await _context.SparePartOrderBatches
                .Select(o => o.CreatedAt.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .ToListAsync();

            var currentYear = DateTime.Now.Year;
            if (!years.Contains(currentYear))
            {
                years.Add(currentYear);
                years = years.OrderByDescending(y => y).ToList();
            }

            ViewData["SelectedYear"] = year;
            ViewData["SelectedMonth"] = month;
            ViewData["AvailableYears"] = years;

            return View(batches);
        }

        // GET: /ProductSearch/SavedBatchDetails
        [HttpGet]
        public async Task<IActionResult> SavedBatchDetails(Guid batchId)
        {
            var batch = await _context.SparePartOrderBatches.FirstOrDefaultAsync(b => b.Id == batchId);
            if (batch == null)
            {
                return NotFound("ไม่พบแผ่นงานรายการสั่งซื้อที่ระบุ");
            }

            // User Isolation Check
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            if (!isAdmin && batch.UserId != currentUserId)
            {
                return Forbid();
            }

            var orders = await _context.SparePartOrders
                .Where(o => o.BatchId == batchId)
                .OrderBy(o => o.Id)
                .ToListAsync();

            ViewData["BatchId"] = batchId;
            ViewData["BatchName"] = batch.BatchName;
            ViewData["CreatedAt"] = batch.CreatedAt;
            ViewData["TotalItems"] = batch.ItemCount;
            ViewData["TotalAmount"] = batch.TotalAmount;

            return View(orders);
        }

        // GET: /ProductSearch/ExportSavedBatch
        [HttpGet]
        public async Task<IActionResult> ExportSavedBatch(Guid batchId)
        {
            var batch = await _context.SparePartOrderBatches.FirstOrDefaultAsync(b => b.Id == batchId);
            if (batch == null)
            {
                return NotFound("ไม่พบข้อมูลแผ่นงานเพื่อส่งออก");
            }

            // User Isolation Check
            bool isAdmin = User.IsInRole("Admin");
            string currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            if (!isAdmin && batch.UserId != currentUserId)
            {
                return Forbid();
            }

            var orders = await _context.SparePartOrders
                .Where(o => o.BatchId == batchId)
                .OrderBy(o => o.Id)
                .ToListAsync();

            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("รายการคีย์ข้อมูล");

                // Set Font Name
                worksheet.Style.Font.FontName = "Segoe UI";
                
                // Add header title block
                worksheet.Cell(1, 1).Value = "แผ่นงานคีย์สั่งซื้ออะไหล่";
                worksheet.Cell(1, 1).Style.Font.Bold = true;
                worksheet.Cell(1, 1).Style.Font.FontSize = 15;
                worksheet.Cell(1, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#1E293B");

                worksheet.Cell(2, 1).Value = $"ชื่อแผ่นงาน: {batch.BatchName}";
                worksheet.Cell(2, 1).Style.Font.FontSize = 10;
                worksheet.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#475569");

                worksheet.Cell(3, 1).Value = $"วันที่บันทึกเข้าระบบ: {batch.CreatedAt.ToString("dd MMMM yyyy เวลา HH:mm น.", new System.Globalization.CultureInfo("th-TH"))}";
                worksheet.Cell(3, 1).Style.Font.FontSize = 10;
                worksheet.Cell(3, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#475569");

                // Add header row
                string[] headers = {
                    "ลำดับ", "รหัสสินค้า", "รายการ", "หน่วย", "ราคาต่อหน่วย", "จำนวน", "มูลค่ารวม", "เลขที่อนุมัติ", "หมายเหตุ", "รับ วันที่"
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

                // Style headers
                var headerRange = worksheet.Range(startRow, 1, startRow, headers.Length);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontSize = 11;
                headerRange.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#1E3A8A"); // Navy blue
                headerRange.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;

                int row = startRow + 1;
                for (int i = 0; i < orders.Count; i++)
                {
                    var o = orders[i];
                    worksheet.Row(row).Height = 20;

                    worksheet.Cell(row, 1).Value = i + 1;
                    worksheet.Cell(row, 2).Value = o.ProductCode;
                    worksheet.Cell(row, 3).Value = o.ProductName;
                    worksheet.Cell(row, 4).Value = o.Unit;
                    worksheet.Cell(row, 5).Value = o.UnitPrice;
                    worksheet.Cell(row, 6).Value = o.Quantity;
                    worksheet.Cell(row, 7).Value = o.TotalAmount;
                    worksheet.Cell(row, 8).Value = o.ApprovalNo ?? "-";
                    worksheet.Cell(row, 9).Value = o.Remarks ?? "-";
                    worksheet.Cell(row, 10).Value = o.ReceiveDate.HasValue ? o.ReceiveDate.Value.ToString("dd/MM/yyyy") : "-";

                    // Alignments
                    worksheet.Cell(row, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 2).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 3).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
                    worksheet.Cell(row, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(row, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(row, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(row, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(row, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
                    worksheet.Cell(row, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                    // Numeric formatting
                    worksheet.Cell(row, 5).Style.NumberFormat.Format = "฿#,##0.00";
                    worksheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0";
                    worksheet.Cell(row, 7).Style.NumberFormat.Format = "฿#,##0.00";

                    // Zebra striping for even rows
                    if (i % 2 == 1)
                    {
                        for (int c = 1; c <= headers.Length; c++)
                        {
                            worksheet.Cell(row, c).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#F8FAFC"); // Slate-50
                        }
                    }

                    row++;
                }

                // Add grand total row
                worksheet.Row(row).Height = 22;
                worksheet.Cell(row, 6).Value = "ยอดรวมทั้งหมด";
                worksheet.Cell(row, 6).Style.Font.Bold = true;
                worksheet.Cell(row, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                worksheet.Cell(row, 7).FormulaA1 = $"=SUM(G{startRow + 1}:G{row - 1})";
                worksheet.Cell(row, 7).Style.Font.Bold = true;
                worksheet.Cell(row, 7).Style.NumberFormat.Format = "฿#,##0.00";
                worksheet.Cell(row, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;

                // Highlight total row
                var totalRowRange = worksheet.Range(row, 1, row, headers.Length);
                totalRowRange.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#F1F5F9");
                totalRowRange.Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Double;

                // Add gridlines & borders
                var tableRange = worksheet.Range(startRow, 1, row, headers.Length);
                tableRange.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                tableRange.Style.Border.OutsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("#CBD5E1");
                tableRange.Style.Border.InsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                tableRange.Style.Border.InsideBorderColor = ClosedXML.Excel.XLColor.FromHtml("#E2E8F0");

                worksheet.Columns().AdjustToContents();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    string safeFileName = string.Join("_", batch.BatchName.Split(Path.GetInvalidFileNameChars()));
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"OrderExport_{safeFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                }
            }
        }


        private DateTime? ParseDateNullable(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (DateTime.TryParse(val, out var d)) return d;
            
            string[] formats = { "d/M/yyyy", "d/M/yy", "dd/MM/yyyy", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(val, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d2))
            {
                return d2;
            }
            return null;
        }

        private decimal ParseDecimal(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return 0;
            string clean = val.Replace("฿", "").Replace(",", "").Trim();
            if (decimal.TryParse(clean, out var dec)) return dec;
            return 0;
        }
    }

    public class SaveOrdersRequest
    {
        public Guid? BatchId { get; set; }
        public string BatchName { get; set; } = string.Empty;
        public List<SparePartOrderSaveModel> Orders { get; set; } = new();
    }

    public class SparePartOrderSaveModel
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? Unit { get; set; }
        public string? UnitPrice { get; set; }
        public string? Quantity { get; set; }
        public string? ApprovalNo { get; set; }
        public string? Remarks { get; set; }
        public string? ReceiveDate { get; set; }
    }

    public class SavedBatchViewModel
    {
        public Guid BatchId { get; set; }
        public string BatchName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int TotalItems { get; set; }
        public double TotalAmount { get; set; }
    }
}
