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
using System.Net.Http;
using System.Globalization;
using System.Collections.Generic;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using CostFlow.Services;

namespace CostFlow.Controllers
{
    [Authorize(Roles = "Admin,Dev")]
    public class PriceReferenceController : Controller
    {
        private readonly IProductPriceRepository _priceRepository;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public PriceReferenceController(
            IProductPriceRepository priceRepository,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _priceRepository = priceRepository;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        // GET: /PriceReference
        public async Task<IActionResult> Index()
        {
            var items = await _priceRepository.Query()
                .OrderBy(p => p.ProductCode)
                .ToListAsync();

            return View(items);
        }

        // GET: /PriceReference/SearchApi
        [HttpGet]
        public async Task<IActionResult> SearchApi()
        {
            var items = await _priceRepository.Query()
                .OrderBy(p => p.ProductCode)
                .ToListAsync();

            return Json(items);
        }

        // GET: /PriceReference/Export
        [HttpGet]
        public async Task<IActionResult> Export(string? search)
        {
            var query = _priceRepository.Query();

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

                // Format Header Row (Minimalist - clean borders, no background colors)
                var headerRange = worksheet.Range(1, 1, 1, 8);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontName = "Noto Sans Thai";
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
                    dataRange.Style.Font.FontName = "Noto Sans Thai";
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
                    string fileName = "ราคากลางอ้างอิงสินค้า.xlsx";
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
                }
            }
        }

        // POST: /PriceReference/Create
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        public async Task<IActionResult> Create([FromBody] ProductPrice model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลรหัสสินค้าไม่ถูกต้อง" });
            }

            var code = model.ProductCode.Trim();
            var existing = await _priceRepository.Query().FirstOrDefaultAsync(x => x.ProductCode == code);
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

            await _priceRepository.AddAsync(price);
            await _priceRepository.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/Edit
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        public async Task<IActionResult> Edit([FromBody] ProductPrice model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลรหัสสินค้าไม่ถูกต้อง" });
            }

            var code = model.ProductCode.Trim();
            var existing = await _priceRepository.Query().FirstOrDefaultAsync(x => x.ProductCode == code);
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

            await _priceRepository.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/Delete
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        public async Task<IActionResult> Delete(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                return Json(new { success = false, error = "รหัสสินค้าไม่ถูกต้อง" });
            }

            var code = productCode.Trim();
            var existing = await _priceRepository.Query().FirstOrDefaultAsync(x => x.ProductCode == code);
            if (existing == null)
            {
                return Json(new { success = false, error = "ไม่พบรหัสสินค้าในระบบ" });
            }

            await _priceRepository.DeleteAsync(existing);
            await _priceRepository.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/SyncFromGoogleSheets
        [HttpPost]
        [Authorize(Roles = "Admin,Dev")]
        public async Task<IActionResult> SyncFromGoogleSheets()
        {
            try
            {
                string? csvUrl = _configuration["GoogleSheets:PriceReferenceCsvUrl"];
                if (string.IsNullOrWhiteSpace(csvUrl))
                {
                    csvUrl = "https://docs.google.com/spreadsheets/d/1DJeeOYd1hGFAaRZ7emkdgLys7G88T13cylayh6Za1xc/export?format=csv";
                }

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var response = await client.GetAsync(csvUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return Json(new { success = false, error = $"ไม่สามารถดาวน์โหลดข้อมูลจาก Google Sheets ได้ (HTTP {(int)response.StatusCode})" });
                }

                var csvStream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(csvStream);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    HeaderValidated = null
                });

                await csv.ReadAsync();
                csv.ReadHeader();

                var existingMap = await _priceRepository.Query().ToDictionaryAsync(p => p.ProductCode);

                int addedCount = 0;
                int updatedCount = 0;
                int totalProcessed = 0;

                while (await csv.ReadAsync())
                {
                    string? code = csv.GetField(0)?.Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    string name = csv.GetField(1)?.Trim() ?? "";
                    string unit = csv.GetField(2)?.Trim() ?? "";
                    double price = ParseDoubleSafe(csv.GetField(3));
                    double totalQty = ParseDoubleSafe(csv.GetField(4));
                    double totalValue = ParseDoubleSafe(csv.GetField(5));
                    string sources = csv.GetField(6)?.Trim() ?? "Google Sheets";

                    totalProcessed++;

                    if (existingMap.TryGetValue(code, out var existingItem))
                    {
                        existingItem.ProductName = name;
                        existingItem.Unit = unit;
                        existingItem.PricePerUnit = price;
                        existingItem.TotalQty = totalQty;
                        existingItem.TotalValue = totalValue;
                        existingItem.Sources = sources;
                        updatedCount++;
                    }
                    else
                    {
                        var newItem = new ProductPrice
                        {
                            ProductCode = code,
                            ProductName = name,
                            Unit = unit,
                            PricePerUnit = price,
                            TotalQty = totalQty,
                            TotalValue = totalValue,
                            Sources = sources
                        };
                        await _priceRepository.AddAsync(newItem);
                        existingMap[code] = newItem;
                        addedCount++;
                    }
                }

                await _priceRepository.SaveChangesAsync();

                return Json(new { 
                    success = true, 
                    total = totalProcessed, 
                    added = addedCount, 
                    updated = updatedCount 
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาด: {ex.Message}" });
            }
        }

        private static double ParseDoubleSafe(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0;
            var cleaned = new string(input.Where(c => char.IsDigit(c) || c == '.' || c == '-' || c == '+').ToArray());
            if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            {
                return result;
            }
            return 0;
        }
    }
}
