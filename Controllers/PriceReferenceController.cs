using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace CostFlow.Controllers
{
    [Authorize(Roles = "Admin")]
    public class PriceReferenceController : Controller
    {
        private readonly AppDbContext _context;

        public PriceReferenceController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /PriceReference
        public async Task<IActionResult> Index(string search, int page = 1)
        {
            const int pageSize = 50;
            var query = _context.ProductPrices.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var cleanSearch = search.Trim().ToLower();
                query = query.Where(p => p.ProductCode.ToLower().Contains(cleanSearch) || 
                                         p.ProductName.ToLower().Contains(cleanSearch));
            }

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(p => p.ProductCode)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewData["Search"] = search;
            ViewData["Page"] = page;
            ViewData["TotalPages"] = (totalCount + pageSize - 1) / pageSize;
            ViewData["TotalCount"] = totalCount;

            return View(items);
        }

        // GET: /PriceReference/SearchApi
        [HttpGet]
        public async Task<IActionResult> SearchApi(string search, int page = 1)
        {
            const int pageSize = 50;
            var query = _context.ProductPrices.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var cleanSearch = search.Trim().ToLower();
                query = query.Where(p => p.ProductCode.ToLower().Contains(cleanSearch) || 
                                         p.ProductName.ToLower().Contains(cleanSearch));
            }

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(p => p.ProductCode)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Json(new {
                items = items,
                currentPage = page,
                totalPages = (totalCount + pageSize - 1) / pageSize,
                totalCount = totalCount
            });
        }

        // GET: /PriceReference/Export
        [HttpGet]
        public async Task<IActionResult> Export(string? search)
        {
            var query = _context.ProductPrices.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var cleanSearch = search.Trim().ToLower();
                query = query.Where(p => p.ProductCode.ToLower().Contains(cleanSearch) || 
                                         p.ProductName.ToLower().Contains(cleanSearch));
            }

            var items = await query.OrderBy(p => p.ProductCode).ToListAsync();

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Reference Prices");

                // Set column headers as requested: #, รหัสสินค้า, ชื่อสินค้า / รายการอะไหล่, หน่วย, ราคาต่อหน่วย, จำนวนรวมเบิก, มูลค่ารวมเบิก, แหล่งข้อมูลประวัติสั่งซื้อ
                worksheet.Cell(1, 1).Value = "#";
                worksheet.Cell(1, 2).Value = "รหัสสินค้า";
                worksheet.Cell(1, 3).Value = "ชื่อสินค้า / รายการอะไหล่";
                worksheet.Cell(1, 4).Value = "หน่วย";
                worksheet.Cell(1, 5).Value = "ราคาต่อหน่วย";
                worksheet.Cell(1, 6).Value = "จำนวนรวมเบิก";
                worksheet.Cell(1, 7).Value = "มูลค่ารวมเบิก";
                worksheet.Cell(1, 8).Value = "แหล่งข้อมูลประวัติสั่งซื้อ";

                // Format Header Row (Minimalist - strictly no color as requested: "อย่าใส่สี นะรูปหัวข้อ ตารางไรงี้")
                var headerRange = worksheet.Range(1, 1, 1, 8);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontName = "Sarabun";
                headerRange.Style.Font.FontSize = 11;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                // Populate Data
                int rowIdx = 2;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    worksheet.Cell(rowIdx, 1).Value = i + 1;
                    worksheet.Cell(rowIdx, 2).Value = item.ProductCode;
                    worksheet.Cell(rowIdx, 3).Value = item.ProductName;
                    worksheet.Cell(rowIdx, 4).Value = item.Unit;
                    worksheet.Cell(rowIdx, 5).Value = item.PricePerUnit;
                    worksheet.Cell(rowIdx, 6).Value = item.TotalQty;
                    worksheet.Cell(rowIdx, 7).Value = item.TotalValue;
                    worksheet.Cell(rowIdx, 8).Value = item.Sources;

                    // Alignments
                    worksheet.Cell(rowIdx, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(rowIdx, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(rowIdx, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                    worksheet.Cell(rowIdx, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    worksheet.Cell(rowIdx, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(rowIdx, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(rowIdx, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    worksheet.Cell(rowIdx, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

                    // Formats
                    worksheet.Cell(rowIdx, 5).Style.NumberFormat.Format = "#,##0.00";
                    worksheet.Cell(rowIdx, 6).Style.NumberFormat.Format = "#,##0";
                    worksheet.Cell(rowIdx, 7).Style.NumberFormat.Format = "#,##0.00";

                    // Apply fonts and thin borders
                    var dataRange = worksheet.Range(rowIdx, 1, rowIdx, 8);
                    dataRange.Style.Font.FontName = "Sarabun";
                    dataRange.Style.Font.FontSize = 10;
                    dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

                    rowIdx++;
                }

                worksheet.Columns().AdjustToContents();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    var content = stream.ToArray();
                    string fileName = $"Product_Prices_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
                }
            }
        }

        // POST: /PriceReference/Create
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create([FromBody] ProductPrice model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลรหัสสินค้าไม่ถูกต้อง" });
            }

            var code = model.ProductCode.Trim();
            var existing = await _context.ProductPrices.FindAsync(code);
            if (existing != null)
            {
                return Json(new { success = false, error = "รหัสสินค้านี้มีอยู่ในระบบแล้ว" });
            }

            var price = new ProductPrice
            {
                ProductCode = code,
                ProductName = model.ProductName?.Trim() ?? string.Empty,
                Unit = model.Unit?.Trim() ?? string.Empty,
                PricePerUnit = model.PricePerUnit,
                TotalQty = model.TotalQty,
                TotalValue = model.TotalValue,
                Sources = model.Sources?.Trim() ?? "งานคีย์ระบบ"
            };

            _context.ProductPrices.Add(price);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/Edit
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit([FromBody] ProductPrice model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลรหัสสินค้าไม่ถูกต้อง" });
            }

            var code = model.ProductCode.Trim();
            var existing = await _context.ProductPrices.FindAsync(code);
            if (existing == null)
            {
                return Json(new { success = false, error = "ไม่พบรหัสสินค้าในระบบ" });
            }

            existing.ProductName = model.ProductName?.Trim() ?? string.Empty;
            existing.Unit = model.Unit?.Trim() ?? string.Empty;
            existing.PricePerUnit = model.PricePerUnit;
            existing.TotalQty = model.TotalQty;
            existing.TotalValue = model.TotalValue;
            if (!string.IsNullOrWhiteSpace(model.Sources))
            {
                existing.Sources = model.Sources.Trim();
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/Delete
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                return Json(new { success = false, error = "รหัสสินค้าไม่ถูกต้อง" });
            }

            var code = productCode.Trim();
            var existing = await _context.ProductPrices.FindAsync(code);
            if (existing == null)
            {
                return Json(new { success = false, error = "ไม่พบรหัสสินค้าในระบบ" });
            }

            _context.ProductPrices.Remove(existing);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
    }
}
