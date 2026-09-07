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

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace CostFlow.Controllers
{
    [Authorize]
    public class ProductSearchController : Controller
    {
        private readonly AppDbContext _context;
        private readonly TiDbContext _tiDbContext;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public ProductSearchController(AppDbContext context, TiDbContext tiDbContext, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _tiDbContext = tiDbContext;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task<IActionResult> Index(string? editBatchName = null)
        {
            if (!string.IsNullOrWhiteSpace(editBatchName))
            {
                // Fetch batch data directly from Database
                var batch = await _tiDbContext.SavedOrderBatches
                    .Include(b => b.Items)
                    .FirstOrDefaultAsync(b => b.BatchName == editBatchName);

                if (batch != null)
                {
                    ViewData["EditBatchName"] = editBatchName;
                    var ordersJson = JsonSerializer.Serialize(batch.Items.Select(it => new
                    {
                        ProductCode = it.ProductCode,
                        ProductName = it.ProductName,
                        Unit = it.Unit,
                        UnitPrice = it.UnitPrice,
                        Quantity = it.Quantity,
                        Remarks = it.Remarks
                    }));
                    ViewData["EditOrdersJson"] = ordersJson;
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
                .ToListAsync();

            return Json(matches);
        }

        // POST: /ProductSearch/SaveOrders
        [HttpPost]
        public async Task<IActionResult> SaveOrders([FromBody] SaveOrdersRequest request)
        {
            if (request == null || request.Orders == null || !request.Orders.Any())
            {
                return Json(new { success = false, error = "ไม่มีข้อมูลใบสั่งซื้อที่จะบันทึก" });
            }

            try
            {
                var now = GetThaiNow();
                if (!string.IsNullOrWhiteSpace(request.OrderDate) && DateTime.TryParseExact(request.OrderDate, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsedDate))
                {
                    now = new DateTime(parsedDate.Year, parsedDate.Month, parsedDate.Day, now.Hour, now.Minute, now.Second);
                }
                var batchName = string.IsNullOrWhiteSpace(request.BatchName)
                    ? $"รายการคีย์ข้อมูลวันที่ {now.ToString("dd/MM/yyyy HH:mm")}"
                    : request.BatchName.Trim();

                var parsedOrders = request.Orders.Select(o => new
                {
                    ProductCode = o.ProductCode ?? string.Empty,
                    ProductName = o.ProductName ?? string.Empty,
                    Unit = o.Unit ?? string.Empty,
                    UnitPrice = ParseDecimal(o.UnitPrice),
                    Quantity = ParseDecimal(o.Quantity),
                    Remarks = o.Remarks ?? string.Empty
                }).ToList();

                // Save directly to Database
                var existingBatch = await _tiDbContext.SavedOrderBatches.FirstOrDefaultAsync(b => b.BatchName == batchName);
                DateTime batchCreatedAt = now;
                if (existingBatch != null)
                {
                    batchCreatedAt = existingBatch.CreatedAt; // Preserve original creation time
                    _tiDbContext.SavedOrderBatches.Remove(existingBatch);
                    await _tiDbContext.SaveChangesAsync();
                }

                var newBatch = new CostFlow.Models.TiDb.SavedOrderBatch
                {
                    BatchName = batchName,
                    CreatedAt = batchCreatedAt,
                    TotalItems = parsedOrders.Count,
                    TotalAmount = parsedOrders.Sum(o => o.UnitPrice * o.Quantity)
                };

                _tiDbContext.SavedOrderBatches.Add(newBatch);
                await _tiDbContext.SaveChangesAsync();

                var dbItems = parsedOrders.Select(o => new CostFlow.Models.TiDb.SavedOrderItem
                {
                    BatchId = newBatch.Id,
                    ProductCode = o.ProductCode,
                    ProductName = o.ProductName,
                    Unit = o.Unit,
                    UnitPrice = o.UnitPrice,
                    Quantity = o.Quantity,
                    Remarks = o.Remarks
                }).ToList();

                _tiDbContext.SavedOrderItems.AddRange(dbItems);
                await _tiDbContext.SaveChangesAsync();

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาดในการบันทึกข้อมูล: {ex.Message}" });
            }
        }

        // POST: /ProductSearch/DeleteBatch
        [HttpPost]
        public async Task<IActionResult> DeleteBatch(string batchName)
        {
            if (string.IsNullOrWhiteSpace(batchName))
                return Json(new { success = false, error = "ไม่ระบุชื่อชุดสั่งซื้อที่จะลบ" });

            try
            {
                var dbBatch = await _tiDbContext.SavedOrderBatches.FirstOrDefaultAsync(b => b.BatchName == batchName);
                if (dbBatch != null)
                {
                    _tiDbContext.SavedOrderBatches.Remove(dbBatch);
                    await _tiDbContext.SaveChangesAsync();
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"ลบข้อมูลล้มเหลว: {ex.Message}" });
            }
        }

        // GET: /ProductSearch/SavedOrders
        [HttpGet]
        public async Task<IActionResult> SavedOrders(int? year, int? month, string? batchName)
        {
            var items = new List<SavedOrderItemViewModel>();
            var availableYears = new List<int> { GetThaiNow().Year };
            var availableMonths = new List<int>();
            var availableBatches = new List<string>();

            var priceDict = await _context.ProductPrices
                .AsNoTracking()
                .ToDictionaryAsync(p => p.ProductCode, p => (decimal)p.PricePerUnit, StringComparer.OrdinalIgnoreCase);

            var batches = await _tiDbContext.SavedOrderBatches
                .AsNoTracking()
                .OrderByDescending(b => b.Id)
                .Select(b => new { b.BatchName, b.CreatedAt })
                .ToListAsync();

            foreach (var b in batches)
            {
                if (!string.IsNullOrWhiteSpace(b.BatchName) && !availableBatches.Contains(b.BatchName))
                {
                    availableBatches.Add(b.BatchName);
                }
                
                if (b.CreatedAt.Year > 2000 && !availableYears.Contains(b.CreatedAt.Year))
                {
                    availableYears.Add(b.CreatedAt.Year);
                }
                
                if (!availableMonths.Contains(b.CreatedAt.Month))
                {
                    availableMonths.Add(b.CreatedAt.Month);
                }
            }

            availableYears = availableYears.Distinct().OrderByDescending(y => y).ToList();
            availableMonths = availableMonths.Distinct().OrderBy(m => m).ToList();
            availableBatches = availableBatches.Distinct().OrderBy(b => b).ToList();

            ViewData["SelectedYear"] = year;
            ViewData["SelectedMonth"] = month;
            ViewData["SelectedBatch"] = batchName;
            ViewData["AvailableYears"] = availableYears;
            ViewData["AvailableMonths"] = availableMonths;
            ViewData["AvailableBatches"] = availableBatches;

            return View(items);
        }

        // GET: /ProductSearch/GetSavedOrdersJson
        [HttpGet]
        public async Task<IActionResult> GetSavedOrdersJson(int? year, int? month, string? batchName)
        {
            var items = new List<SavedOrderItemViewModel>();

            var query = _tiDbContext.SavedOrderBatches
                .Include(b => b.Items)
                .AsQueryable();

            if (year.HasValue && year.Value > 0)
            {
                query = query.Where(b => b.CreatedAt.Year == year.Value);
            }
            if (month.HasValue && month.Value > 0)
            {
                query = query.Where(b => b.CreatedAt.Month == month.Value);
            }
            if (!string.IsNullOrWhiteSpace(batchName))
            {
                query = query.Where(b => b.BatchName == batchName);
            }

            // Order batches by original physical insertion order (Id descending) to perfectly match Google Sheet
            var batches = await query.OrderByDescending(b => b.Id).ToListAsync();

            foreach (var b in batches)
            {
                // Order items exactly as they were inserted (Id descending) so the latest item in the batch shows at the top
                var sortedItems = b.Items.OrderByDescending(i => i.Id).ToList();
                foreach (var item in sortedItems)
                {
                    items.Add(new SavedOrderItemViewModel
                    {
                        BatchName = b.BatchName,
                        CreatedAt = b.CreatedAt,
                        ProductCode = item.ProductCode,
                        ProductName = item.ProductName,
                        Unit = item.Unit,
                        UnitPrice = item.UnitPrice,
                        Quantity = item.Quantity,
                        Remarks = item.Remarks
                    });
                }
            }

            var result = new
            {
                items = items.Select(i => new
                {
                    batchName = i.BatchName,
                    createdAtStr = i.CreatedAt == default ? "-" : i.CreatedAt.ToString("dd/MM/yyyy HH:mm"),
                    productCode = i.ProductCode,
                    productName = i.ProductName,
                    unit = i.Unit,
                    unitPrice = i.UnitPrice,
                    quantity = i.Quantity,
                    totalAmount = i.TotalAmount,
                    remarks = string.IsNullOrWhiteSpace(i.Remarks) ? "-" : i.Remarks
                }).ToList(),
                totalCount = items.Count,
                totalQuantity = items.Sum(i => i.Quantity),
                totalAmount = items.Sum(i => i.TotalAmount)
            };

            return Json(result);
        }

        // POST: /ProductSearch/MigrateToTiDb
        [HttpPost]
        public async Task<IActionResult> MigrateToTiDb()
        {
            var priceDict = await _context.ProductPrices
                .AsNoTracking()
                .ToDictionaryAsync(p => p.ProductCode, p => (decimal)p.PricePerUnit, StringComparer.OrdinalIgnoreCase);

            string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
            if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
            {
                return Json(new { success = false, error = "ยังไม่ได้ตั้งค่า Google Sheets API URL" });
            }

            int batchesImported = 0;
            int itemsImported = 0;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(120);
                var response = await client.GetAsync(appScriptUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("batches", out var batchesEl))
                    {
                        var rawBatches = new List<(string Name, DateTime Date, JsonElement InlineItems)>();
                        foreach (var b in batchesEl.EnumerateArray())
                        {
                            var bName = GetStringProp(b, "BatchName");
                            var createdAtStr = GetStringProp(b, "CreatedAt");
                            var createdAt = ParseDateNullable(createdAtStr) ?? DateTime.MinValue;

                            // Skip if already in TiDB
                            bool exists = await _tiDbContext.SavedOrderBatches.AnyAsync(tb => tb.BatchName == bName);
                            if (exists) continue;

                            JsonElement itemsEl = default;
                            bool hasInline = b.TryGetProperty("Orders", out itemsEl) || b.TryGetProperty("items", out itemsEl);
                            rawBatches.Add((bName, createdAt, hasInline ? itemsEl : default));
                        }

                        foreach (var bInfo in rawBatches)
                        {
                            var itemsList = new List<CostFlow.Models.TiDb.SavedOrderItem>();
                            if (bInfo.InlineItems.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in bInfo.InlineItems.EnumerateArray())
                                {
                                    var parsed = ParseSavedItem(item, bInfo.Name, bInfo.Date, priceDict);
                                    itemsList.Add(new CostFlow.Models.TiDb.SavedOrderItem
                                    {
                                        ProductCode = parsed.ProductCode,
                                        ProductName = parsed.ProductName,
                                        Unit = parsed.Unit,
                                        UnitPrice = parsed.UnitPrice,
                                        Quantity = parsed.Quantity,
                                        Remarks = parsed.Remarks,
                                        IsReceived = parsed.IsReceived,
                                        ReceiveDate = ParseDateNullable(parsed.ReceiveDate)
                                    });
                                }
                            }
                            else
                            {
                                try
                                {
                                    var detailRes = await client.GetAsync($"{appScriptUrl}?action=getBatchDetails&batchName={Uri.EscapeDataString(bInfo.Name)}");
                                    if (detailRes.IsSuccessStatusCode)
                                    {
                                        var dJson = await detailRes.Content.ReadAsStringAsync();
                                        using var dDoc = JsonDocument.Parse(dJson);
                                        var dRoot = dDoc.RootElement;
                                        if (dRoot.TryGetProperty("items", out var dItemsEl) && dItemsEl.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var item in dItemsEl.EnumerateArray())
                                            {
                                                var parsed = ParseSavedItem(item, bInfo.Name, bInfo.Date, priceDict);
                                                itemsList.Add(new CostFlow.Models.TiDb.SavedOrderItem
                                                {
                                                    ProductCode = parsed.ProductCode,
                                                    ProductName = parsed.ProductName,
                                                    Unit = parsed.Unit,
                                                    UnitPrice = parsed.UnitPrice,
                                                    Quantity = parsed.Quantity,
                                                    Remarks = parsed.Remarks,
                                                    IsReceived = parsed.IsReceived,
                                                    ReceiveDate = ParseDateNullable(parsed.ReceiveDate)
                                                });
                                            }
                                        }
                                    }
                                }
                                catch { /* skip this batch if it fails */ }
                            }

                            if (itemsList.Any())
                            {
                                var newBatch = new CostFlow.Models.TiDb.SavedOrderBatch
                                {
                                    BatchName = bInfo.Name,
                                    CreatedAt = bInfo.Date,
                                    TotalItems = itemsList.Count,
                                    TotalAmount = itemsList.Sum(i => i.Quantity * i.UnitPrice),
                                    Items = itemsList
                                };
                                _tiDbContext.SavedOrderBatches.Add(newBatch);
                                await _tiDbContext.SaveChangesAsync();
                                batchesImported++;
                                itemsImported += itemsList.Count;
                            }
                        }
                    }
                }

                return Json(new { success = true, message = $"นำเข้าข้อมูลสำเร็จ จำนวน {batchesImported} แบตช์ (รวม {itemsImported} รายการ)" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        private SavedOrderItemViewModel ParseSavedItem(JsonElement item, string bName, DateTime createdAt, Dictionary<string, decimal>? priceDict = null)
        {
            var pCode = GetStringProp(item, "ProductCode", "productCode", "Code", "code", "รหัสสินค้า");
            var pName = GetStringProp(item, "ProductName", "productName", "Name", "name", "ชื่อสินค้า", "ชื่อสินค้า / รายการอะไหล่");
            var unit = GetStringProp(item, "Unit", "unit", "หน่วย");
            var unitPriceStr = GetStringProp(item, "UnitPrice", "unitPrice", "Price", "price", "PricePerUnit", "Unit Price", "ราคา/หน่วย", "ราคาต่อหน่วย", "Cost", "cost");
            var quantityStr = GetStringProp(item, "Quantity", "quantity", "Qty", "qty", "จำนวน");
            var totalAmountStr = GetStringProp(item, "TotalAmount", "totalAmount", "Total", "total", "TotalAmount", "ราคารวม", "TotalPrice", "totalPrice", "มูลค่ารวม");
            var remarks = GetStringProp(item, "Remarks", "remarks", "Remark", "remark", "หมายเหตุ", "Note", "note");
            var isReceived = item.TryGetProperty("IsReceived", out var ir) && (ir.ValueKind == JsonValueKind.True || (ir.ValueKind == JsonValueKind.String && ir.GetString()?.ToLower() == "true"));
            var receiveDateStr = GetStringProp(item, "ReceiveDate", "receiveDate", "วันที่รับ");

            var unitPrice = ParseDecimal(unitPriceStr);
            var quantity = ParseDecimal(quantityStr);
            var totalAmount = ParseDecimal(totalAmountStr);

            if (unitPrice == 0 && quantity > 0 && totalAmount > 0)
            {
                unitPrice = totalAmount / quantity;
            }

            if (unitPrice == 0 && !string.IsNullOrWhiteSpace(pCode) && priceDict != null && priceDict.TryGetValue(pCode, out var dbPrice))
            {
                unitPrice = dbPrice;
            }

            return new SavedOrderItemViewModel
            {
                BatchName = bName,
                CreatedAt = createdAt,
                ProductCode = pCode,
                ProductName = pName,
                Unit = unit,
                UnitPrice = unitPrice,
                Quantity = quantity,
                Remarks = remarks,
                IsReceived = isReceived,
                ReceiveDate = receiveDateStr
            };
        }

        // POST: /ProductSearch/UpdateItemQuantity
        [HttpPost]
        public async Task<IActionResult> UpdateItemQuantity([FromBody] UpdateQuantityRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BatchName) || string.IsNullOrWhiteSpace(request.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลไม่ถูกต้อง" });
            }

            if (request.NewQuantity <= 0)
            {
                return Json(new { success = false, error = "จำนวนต้องมากกว่า 0" });
            }

            try
            {
                // ดึงข้อมูล Batch และ Items ทั้งหมดจาก TiDB
                var dbBatch = await _tiDbContext.SavedOrderBatches
                    .Include(b => b.Items)
                    .FirstOrDefaultAsync(b => b.BatchName == request.BatchName);

                if (dbBatch == null)
                {
                    return Json(new { success = false, error = "ไม่พบชุดสั่งซื้อนี้ในฐานข้อมูลระบบ" });
                }

                var itemToUpdate = dbBatch.Items.FirstOrDefault(i => i.ProductCode == request.ProductCode);
                if (itemToUpdate == null)
                {
                    return Json(new { success = false, error = "ไม่พบรายการสินค้าที่ระบุ" });
                }

                // 1. อัปเดตข้อมูลใน Memory ของ TiDB ก่อน
                itemToUpdate.Quantity = request.NewQuantity;
                if (request.NewPrice.HasValue)
                {
                    itemToUpdate.UnitPrice = request.NewPrice.Value;
                }
                dbBatch.TotalAmount = dbBatch.Items.Sum(i => i.Quantity * i.UnitPrice);
                // Do NOT bump CreatedAt to preserve chronological history order

                // 2. Save ลง TiDB อย่างเดียว ไม่ต้องลง Google Sheets ตามที่คุณต้องการ
                try
                {
                    _tiDbContext.SavedOrderItems.Update(itemToUpdate);
                    _tiDbContext.SavedOrderBatches.Update(dbBatch);
                    await _tiDbContext.SaveChangesAsync();
                }
                catch (Exception dbEx)
                {
                    return Json(new { success = false, error = $"บันทึกข้อมูลล้มเหลว: {dbEx.Message}" });
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาด: {ex.Message}" });
            }
        }

        // POST: /ProductSearch/DeleteSavedOrderItem
        [HttpPost]
        public async Task<IActionResult> DeleteSavedOrderItem([FromBody] DeleteOrderItemRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BatchName) || string.IsNullOrWhiteSpace(request.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลไม่ถูกต้อง" });
            }

            try
            {
                // ดึงข้อมูล Batch จาก TiDB
                var dbBatch = await _tiDbContext.SavedOrderBatches
                    .Include(b => b.Items)
                    .FirstOrDefaultAsync(b => b.BatchName == request.BatchName);

                if (dbBatch == null)
                {
                    return Json(new { success = false, error = "ไม่พบชุดสั่งซื้อนี้ในฐานข้อมูลระบบ" });
                }

                var itemToDelete = dbBatch.Items.FirstOrDefault(i => i.ProductCode == request.ProductCode);
                if (itemToDelete == null)
                {
                    return Json(new { success = false, error = "ไม่พบรายการสินค้าที่ระบุในฐานข้อมูล" });
                }

                // 1. อัปเดตข้อมูลใน Memory ของ TiDB ก่อน
                dbBatch.Items.Remove(itemToDelete);
                dbBatch.TotalItems = dbBatch.Items.Count;
                dbBatch.TotalAmount = dbBatch.Items.Sum(i => i.Quantity * i.UnitPrice);
                // Do NOT bump CreatedAt to preserve chronological history order

                // 2. เซฟลง TiDB อย่างเดียว ไม่ต้องลง Google Sheets
                try
                {
                    if (dbBatch.Items.Count == 0)
                    {
                        _tiDbContext.SavedOrderBatches.Remove(dbBatch);
                    }
                    else
                    {
                        _tiDbContext.SavedOrderBatches.Update(dbBatch);
                    }
                    _tiDbContext.SavedOrderItems.Remove(itemToDelete);
                    await _tiDbContext.SaveChangesAsync();
                }
                catch (Exception dbEx)
                {
                    return Json(new { success = false, error = $"บันทึกข้อมูลล้มเหลว: {dbEx.Message}" });
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาด: {ex.Message}" });
            }
        }

        private static string GetStringProp(JsonElement element, params string[] propNames)
        {
            if (element.ValueKind != JsonValueKind.Object) return "";

            foreach (var name in propNames)
            {
                if (element.TryGetProperty(name, out var prop))
                {
                    var val = GetJsonValString(prop);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }

            foreach (var p in element.EnumerateObject())
            {
                foreach (var name in propNames)
                {
                    if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        p.Name.Replace(" ", "").Replace("_", "").Equals(name.Replace(" ", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        var val = GetJsonValString(p.Value);
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }

            return "";
        }

        private static string GetJsonValString(JsonElement prop)
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? "",
                JsonValueKind.Number => prop.GetRawText(),
                JsonValueKind.Null => "",
                JsonValueKind.Undefined => "",
                _ => prop.ToString() ?? ""
            };
        }

        private static DateTime GetThaiNow()
        {
            var utcNow = DateTime.UtcNow;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            }
            catch
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
                    return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
                }
                catch
                {
                    return utcNow.AddHours(7);
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
            if (decimal.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec)) return dec;
            if (decimal.TryParse(clean, out var dec2)) return dec2;
            return 0;
        }
    }

    public class SaveOrdersRequest
    {
        public string BatchName { get; set; } = string.Empty;
        public string? OrderDate { get; set; }
        public List<SparePartOrderSaveModel> Orders { get; set; } = new();
    }

    public class SparePartOrderSaveModel
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? Unit { get; set; }
        public string? UnitPrice { get; set; }
        public string? Quantity { get; set; }
        public string? Remarks { get; set; }
    }

    public class SavedBatchViewModel
    {
        public string BatchName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int TotalItems { get; set; }
        public double TotalAmount { get; set; }
    }

    public class SavedOrderItemViewModel
    {
        public string BatchName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal TotalAmount => UnitPrice * Quantity;
        public string Remarks { get; set; } = string.Empty;
        public bool IsReceived { get; set; }
        public string? ReceiveDate { get; set; }
    }

    public class UpdateQuantityRequest
    {
        public string BatchName { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public decimal NewQuantity { get; set; }
        public decimal? NewPrice { get; set; }
    }

    public class DeleteOrderItemRequest
    {
        public string BatchName { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
    }
}
