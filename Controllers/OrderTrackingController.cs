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

            string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
            if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
            {
                return Json(new { success = false, error = "ยังไม่ได้ตั้งค่า Google Sheets API URL" });
            }

            try
            {
                var payload = new
                {
                    action = "markItemsReceived",
                    batchName = request.BatchName,
                    productCodes = request.ProductCodes,
                    receiveDate = request.ReceiveDate,
                    markedBy = User.Identity?.Name ?? "Unknown"
                };

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);

                var jsonString = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(appScriptUrl, content);
                if (!response.IsSuccessStatusCode)
                {
                    return Json(new { success = false, error = $"เกิดข้อผิดพลาดจาก Google Sheets (HTTP {(int)response.StatusCode})" });
                }

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                bool isSuccess = root.TryGetProperty("success", out var succProp) && succProp.GetBoolean();
                if (!isSuccess)
                {
                    string errorMsg = root.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? "" : "การบันทึกลง Google Sheets ล้มเหลว";
                    return Json(new { success = false, error = errorMsg });
                }

                // Update TiDB
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
}
