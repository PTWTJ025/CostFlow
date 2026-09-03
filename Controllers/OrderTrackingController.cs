using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;

namespace CostFlow.Controllers
{
    [Authorize]
    public class OrderTrackingController : Controller
    {
        private readonly AppDbContext _context;
        private readonly TiDbContext _tiDbContext;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public OrderTrackingController(AppDbContext context, TiDbContext tiDbContext, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _tiDbContext = tiDbContext;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var availableYears = new List<int> { DateTime.Now.Year };

            var dbBatches = await _tiDbContext.SavedOrderBatches
                .Include(b => b.Items)
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync();

            // Populate available years for dropdown
            var distinctYears = await _tiDbContext.SavedOrderBatches.Select(b => b.CreatedAt.Year).Distinct().ToListAsync();
            foreach (var y in distinctYears)
            {
                if (!availableYears.Contains(y)) availableYears.Add(y);
            }

            // Build flat item list
            var flatItems = new List<FlatOrderItemViewModel>();
            foreach (var b in dbBatches)
            {
                int totalItems = b.Items.Count;
                double batchTotal = (double)b.Items.Sum(i => i.Quantity * i.UnitPrice);

                foreach (var it in b.Items)
                {
                    flatItems.Add(new FlatOrderItemViewModel
                    {
                        ItemId = it.Id,
                        BatchName = b.BatchName,
                        BatchCreatedAt = b.CreatedAt,
                        BatchTotalItems = totalItems,
                        BatchTotalAmount = batchTotal,
                        ProductCode = it.ProductCode,
                        ProductName = it.ProductName,
                        Quantity = it.Quantity.ToString("0.##"),
                        Unit = it.Unit,
                        IsReceived = it.IsReceived,
                        ReceiveDate = it.ReceiveDate?.ToString("dd/MM/yyyy HH:mm")
                    });
                }
            }

            availableYears = availableYears.OrderByDescending(y => y).ToList();
            ViewData["AvailableYears"] = availableYears;

            return View(flatItems);
        }

        [HttpGet]
        public async Task<IActionResult> Detail(string batchName)
        {
            if (string.IsNullOrWhiteSpace(batchName)) return RedirectToAction("Index");
            
            var items = new List<TrackingItemViewModel>();
            
            var batch = await _tiDbContext.SavedOrderBatches
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.BatchName == batchName);

            if (batch != null)
            {
                foreach (var it in batch.Items)
                {
                    items.Add(new TrackingItemViewModel
                    {
                        ProductCode = it.ProductCode,
                        ProductName = it.ProductName,
                        Quantity = it.Quantity.ToString("0.##"),
                        Unit = it.Unit,
                        IsReceived = it.IsReceived,
                        ReceiveDate = it.ReceiveDate?.ToString("dd/MM/yyyy HH:mm")
                    });
                }
            }
            
            ViewData["BatchName"] = batchName;
            return View(items);
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

        private static int ParseInt(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return 0;
            if (int.TryParse(val.Trim(), out var i)) return i;
            if (double.TryParse(val.Trim(), out var d)) return (int)d;
            return 0;
        }

        private static double ParseDouble(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return 0;
            string clean = val.Replace("฿", "").Replace(",", "").Trim();
            if (double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            if (double.TryParse(clean, out var d2)) return d2;
            return 0;
        }

        [HttpPost]
        public async Task<IActionResult> MarkItemsReceived([FromBody] MarkItemsRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BatchName) || request.ProductCodes == null || !request.ProductCodes.Any())
            {
                return Json(new { success = false, error = "ข้อมูลไม่ครบถ้วน" });
            }

            try
            {
                // Update Database (TiDB)
                var batch = await _tiDbContext.SavedOrderBatches
                    .Include(b => b.Items)
                    .FirstOrDefaultAsync(b => b.BatchName == request.BatchName);

                if (batch != null)
                {
                    DateTime? parsedReceiveDate = null;
                    if (!string.IsNullOrWhiteSpace(request.ReceiveDate))
                    {
                        var parts = request.ReceiveDate.Split(' ')[0].Split('/'); // expects dd/MM/yyyy
                        if (parts.Length == 3 && int.TryParse(parts[0], out int d) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int y))
                        {
                            try
                            {
                                parsedReceiveDate = new DateTime(y, m, d);
                            }
                            catch { }
                        }
                    }

                    foreach (var it in batch.Items)
                    {
                        if (request.ProductCodes.Contains(it.ProductCode))
                        {
                            it.IsReceived = true;
                            it.ReceiveDate = parsedReceiveDate ?? DateTime.Now;
                        }
                    }
                    await _tiDbContext.SaveChangesAsync();
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาด: {ex.Message}" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> MarkBulkReceived([FromBody] BulkMarkItemsRequest request)
        {
            if (request == null || request.Items == null || !request.Items.Any())
            {
                return Json(new { success = false, error = "กรุณาเลือกรายการที่ต้องการรับสินค้า" });
            }

            DateTime? parsedReceiveDate = null;
            if (!string.IsNullOrWhiteSpace(request.ReceiveDate))
            {
                var parts = request.ReceiveDate.Split(' ')[0].Split('/'); // expects dd/MM/yyyy
                if (parts.Length == 3 && int.TryParse(parts[0], out int d) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int y))
                {
                    try
                    {
                        parsedReceiveDate = new DateTime(y, m, d);
                    }
                    catch { }
                }
            }
            var receiveDateTime = parsedReceiveDate ?? DateTime.Now;

            try
            {
                var itemIds = request.Items.Where(x => x.ItemId > 0).Select(x => x.ItemId).Distinct().ToList();
                if (itemIds.Any())
                {
                    var dbItems = await _tiDbContext.SavedOrderItems.Where(i => itemIds.Contains(i.Id)).ToListAsync();
                    foreach (var it in dbItems)
                    {
                        it.IsReceived = true;
                        it.ReceiveDate = receiveDateTime;
                    }
                }

                // Fallback / backup by BatchName + ProductCode
                var batchGroups = request.Items.GroupBy(x => x.BatchName);
                foreach (var grp in batchGroups)
                {
                    if (string.IsNullOrWhiteSpace(grp.Key)) continue;
                    var codes = grp.Select(x => x.ProductCode).Distinct().ToList();
                    var batch = await _tiDbContext.SavedOrderBatches
                        .Include(b => b.Items)
                        .FirstOrDefaultAsync(b => b.BatchName == grp.Key);
                    if (batch != null)
                    {
                        foreach (var it in batch.Items.Where(i => codes.Contains(i.ProductCode)))
                        {
                            it.IsReceived = true;
                            it.ReceiveDate = receiveDateTime;
                        }
                    }
                }

                await _tiDbContext.SaveChangesAsync();
                return Json(new { success = true, count = request.Items.Count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาดในการบันทึกฐานข้อมูล: {ex.Message}" });
            }
        }
    }

    public class TrackingBatchViewModel
    {
        public string BatchName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int TotalItems { get; set; }
        public int ReceivedItems { get; set; }
        public double TotalAmount { get; set; }
        public bool IsReceived { get; set; }
        public string? ReceiveDate { get; set; }
    }

    public class FlatOrderItemViewModel
    {
        // Batch info
        public int ItemId { get; set; }
        public string BatchName { get; set; } = string.Empty;
        public DateTime BatchCreatedAt { get; set; }
        public int BatchTotalItems { get; set; }
        public double BatchTotalAmount { get; set; }

        // Item info
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public bool IsReceived { get; set; }
        public string? ReceiveDate { get; set; }
    }

    public class TrackingItemViewModel
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public bool IsReceived { get; set; }
        public string? ReceiveDate { get; set; }
    }

    public class MarkItemsRequest
    {
        public string BatchName { get; set; } = string.Empty;
        public List<string> ProductCodes { get; set; } = new();
        public string ReceiveDate { get; set; } = string.Empty;
    }

    public class BulkMarkItemsRequest
    {
        public List<BulkItemDto> Items { get; set; } = new();
        public string ReceiveDate { get; set; } = string.Empty;
    }

    public class BulkItemDto
    {
        public int ItemId { get; set; }
        public string BatchName { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
    }
}
