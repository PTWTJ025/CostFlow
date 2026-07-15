using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using ClosedXML.Excel;
using ClosedXML.Graphics;

namespace CostFlow.Controllers
{
    public class MockGraphicEngine : IXLGraphicEngine
    {
        public XLPictureInfo GetPictureInfo(Stream imageStream, ClosedXML.Excel.Drawings.XLPictureFormat expectedFormat)
        {
            throw new NotImplementedException();
        }

        public double GetTextHeight(IXLFontBase font, double dpiY)
        {
            return font.FontSize * 1.2;
        }

        public double GetTextWidth(string text, IXLFontBase font, double dpiX)
        {
            return text.Length * (font.FontSize * 0.6);
        }

        public double GetMaxDigitWidth(IXLFontBase font, double dpiX)
        {
            return font.FontSize * 0.6;
        }

        public double GetDescent(IXLFontBase font, double dpiY)
        {
            return 0.0;
        }

        public GlyphBox GetGlyphBox(ReadOnlySpan<int> graphemeCluster, IXLFontBase font, Dpi dpi)
        {
            return new GlyphBox(0f, 0f, 0f);
        }
    }

    [Authorize(Roles = "Admin")]
    public class MonthlyExpenseController : Controller
    {
        private readonly AppDbContext _context;

        public MonthlyExpenseController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /MonthlyExpense/Estimation
        [HttpGet]
        public IActionResult Estimation()
        {
            ViewData["HeaderTitle"] = "ประมาณการค่าใช้จ่ายประจำเดือน";
            return View();
        }

        // GET: /MonthlyExpense/Calculation
        [HttpGet]
        public IActionResult Calculation()
        {
            ViewData["HeaderTitle"] = "คำนวณค่าใช้จ่ายประจำเดือน";
            return View();
        }

        // GET: /MonthlyExpense/GetProductsJson
        [HttpGet]
        public async Task<IActionResult> GetProductsJson(string? search, int page = 1, int pageSize = 20)
        {
            var query = _context.ProductPrices.AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                string searchLower = search.Trim().ToLower();
                query = query.Where(p => p.ProductCode.Contains(searchLower) || p.ProductName.ToLower().Contains(searchLower));
            }

            int totalCount = await query.CountAsync();
            int totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            if (page < 1) page = 1;
            if (page > totalPages && totalPages > 0) page = totalPages;

            var products = await query
                .OrderBy(p => p.ProductCode)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Json(new
            {
                products,
                page,
                totalPages,
                totalCount
            });
        }

        public class ExportRequest
        {
            public string EstimationMonth { get; set; } = "";
            public List<SelectedProductDto> Items { get; set; } = new();
        }

        public class SelectedProductDto
        {
            public string ProductCode { get; set; } = "";
            public string ProductName { get; set; } = "";
            public string Unit { get; set; } = "";
            public double PricePerUnit { get; set; }
            public double Quantity { get; set; }
        }

        // POST: /MonthlyExpense/ExportEstimationExcel
        [HttpPost]
        public IActionResult ExportEstimationExcel([FromBody] ExportRequest request)
        {
            if (request == null || request.Items == null || !request.Items.Any())
            {
                return BadRequest("ไม่มีรายการสินค้าที่เลือกสำหรับส่งออก");
            }

            // Register Mock Graphic Engine for environments without system fonts installed
            LoadOptions.DefaultGraphicEngine = new MockGraphicEngine();

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("ประมาณการค่าใช้จ่าย");

            // Title
            ws.Cell(1, 1).Value = $"ใบประมาณการสั่งซื้อและค่าใช้จ่ายประจำเดือน {request.EstimationMonth}";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.FontName = "Sarabun";
            ws.Range(1, 1, 1, 6).Merge();

            // Headers
            ws.Cell(3, 1).Value = "ลำดับ";
            ws.Cell(3, 2).Value = "รหัสสินค้า";
            ws.Cell(3, 3).Value = "ชื่อสินค้า / รายการอะไหล่";
            ws.Cell(3, 4).Value = "หน่วย";
            ws.Cell(3, 5).Value = "ราคาต่อหน่วย";
            ws.Cell(3, 6).Value = "จำนวนประมาณการสั่ง";
            ws.Cell(3, 7).Value = "มูลค่ารวมประมาณการ";

            var headerRange = ws.Range(3, 1, 3, 7);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Font.FontSize = 11;
            headerRange.Style.Font.FontName = "Sarabun";
            headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            int rowIdx = 4;
            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                ws.Cell(rowIdx, 1).Value = i + 1;
                ws.Cell(rowIdx, 2).Value = item.ProductCode;
                ws.Cell(rowIdx, 3).Value = item.ProductName;
                ws.Cell(rowIdx, 4).Value = item.Unit;
                ws.Cell(rowIdx, 5).Value = item.PricePerUnit;
                ws.Cell(rowIdx, 6).Value = item.Quantity;
                
                // Formula for total: Price * Qty
                ws.Cell(rowIdx, 7).FormulaA1 = $"E{rowIdx}*F{rowIdx}";

                // Formatting
                ws.Cell(rowIdx, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(rowIdx, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(rowIdx, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(rowIdx, 5).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(rowIdx, 6).Style.NumberFormat.Format = "#,##0";
                ws.Cell(rowIdx, 7).Style.NumberFormat.Format = "#,##0.00";

                var rowRange = ws.Range(rowIdx, 1, rowIdx, 7);
                rowRange.Style.Font.FontName = "Sarabun";
                rowRange.Style.Font.FontSize = 10;
                rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                rowIdx++;
            }

            // Total Summary Row
            ws.Cell(rowIdx, 1).Value = "รวมทั้งหมด";
            ws.Range(rowIdx, 1, rowIdx, 6).Merge();
            ws.Cell(rowIdx, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(rowIdx, 1).Style.Font.Bold = true;
            
            ws.Cell(rowIdx, 7).FormulaA1 = $"SUM(G4:G{rowIdx - 1})";
            ws.Cell(rowIdx, 7).Style.Font.Bold = true;
            ws.Cell(rowIdx, 7).Style.NumberFormat.Format = "#,##0.00";

            var summaryRange = ws.Range(rowIdx, 1, rowIdx, 7);
            summaryRange.Style.Font.FontName = "Sarabun";
            summaryRange.Style.Font.FontSize = 11;
            summaryRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            summaryRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var content = stream.ToArray();
            
            string fileName = $"Estimation_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
    }
}
