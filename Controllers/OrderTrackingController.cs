using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Authorization;

namespace CostFlow.Controllers
{
    [Authorize]
    public class OrderTrackingController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public OrderTrackingController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? year, int? month, string? batchName)
        {
            var batches = new List<TrackingBatchViewModel>();
            var availableYears = new List<int> { DateTime.Now.Year };

            string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
            if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
            {
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(30);
                    
                    var response = await client.GetAsync($"{appScriptUrl}?action=getTracking");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("batches", out var batchesEl))
                        {
                            foreach (var b in batchesEl.EnumerateArray())
                            {
                                var name = GetStringProp(b, "BatchName");
                                var createdAtStr = GetStringProp(b, "CreatedAt");
                                var totalItems = b.TryGetProperty("TotalItems", out var tip) && tip.ValueKind == JsonValueKind.Number ? tip.GetInt32() : 0;
                                var totalAmount = b.TryGetProperty("TotalAmount", out var tap) && tap.ValueKind == JsonValueKind.Number ? tap.GetDouble() : 0;
                                
                                var receiveDateStr = GetStringProp(b, "ReceiveDate");
                                var isReceived = !string.IsNullOrEmpty(receiveDateStr);

                                DateTime.TryParse(createdAtStr, out var createdAt);

                                if (year.HasValue && year.Value > 0 && createdAt.Year != year.Value) continue;
                                if (month.HasValue && month.Value > 0 && createdAt.Month != month.Value) continue;

                                batches.Add(new TrackingBatchViewModel
                                {
                                    BatchName = name,
                                    CreatedAt = createdAt,
                                    TotalItems = totalItems,
                                    TotalAmount = totalAmount,
                                    IsReceived = isReceived,
                                    ReceiveDate = receiveDateStr
                                });

                                if (!availableYears.Contains(createdAt.Year))
                                    availableYears.Add(createdAt.Year);
                            }
                        }
                    }
                }
                catch { /* fail gracefully */ }
            }

            availableYears = availableYears.OrderByDescending(y => y).ToList();
            ViewData["SelectedYear"] = year;
            ViewData["SelectedMonth"] = month;
            ViewData["SelectedBatch"] = batchName;
            ViewData["AvailableYears"] = availableYears;

            return View(batches);
        }

        [HttpGet]
        public async Task<IActionResult> Detail(string batchName)
        {
            if (string.IsNullOrWhiteSpace(batchName)) return RedirectToAction("Index");
            
            var items = new List<TrackingItemViewModel>();
            
            string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
            if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
            {
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(30);
                    
                    var response = await client.GetAsync($"{appScriptUrl}?action=getBatchDetails&batchName={Uri.EscapeDataString(batchName)}");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("items", out var itemsEl))
                        {
                            foreach (var it in itemsEl.EnumerateArray())
                            {
                                items.Add(new TrackingItemViewModel
                                {
                                    ProductCode = GetStringProp(it, "ProductCode"),
                                    ProductName = GetStringProp(it, "ProductName"),
                                    Quantity = GetStringProp(it, "Quantity"),
                                    Unit = GetStringProp(it, "Unit"),
                                    IsReceived = it.TryGetProperty("IsReceived", out var ir) && (ir.ValueKind == JsonValueKind.True || (ir.ValueKind == JsonValueKind.String && ir.GetString()?.ToLower() == "true")),
                                    ReceiveDate = GetStringProp(it, "ReceiveDate")
                                });
                            }
                        }
                    }
                }
                catch { /* fail gracefully */ }
            }
            
            ViewData["BatchName"] = batchName;
            return View(items);
        }

        private static string GetStringProp(JsonElement element, string propName)
        {
            if (!element.TryGetProperty(propName, out var prop)) return "";
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? "",
                JsonValueKind.Number => prop.GetRawText(),
                JsonValueKind.Null => "",
                JsonValueKind.Undefined => "",
                _ => prop.ToString() ?? ""
            };
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
        public double TotalAmount { get; set; }
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
