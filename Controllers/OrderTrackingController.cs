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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public OrderTrackingController(AppDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? year, int? month, string? batchName)
        {
            var batches = new List<TrackingBatchViewModel>();
            var availableYears = new List<int> { DateTime.Now.Year };

            var priceDict = await _context.ProductPrices
                .AsNoTracking()
                .ToDictionaryAsync(p => p.ProductCode, p => p.PricePerUnit, StringComparer.OrdinalIgnoreCase);

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
                            var rawList = new List<TrackingBatchViewModel>();
                            foreach (var b in batchesEl.EnumerateArray())
                            {
                                var name = GetStringProp(b, "BatchName", "batchName", "Name", "name");
                                var createdAtStr = GetStringProp(b, "CreatedAt", "createdAt", "Date", "date");
                                
                                var totalItemsStr = GetStringProp(b, "TotalItems", "totalItems", "ItemsCount", "itemsCount", "Count");
                                var totalItems = ParseInt(totalItemsStr);

                                var receivedItemsStr = GetStringProp(b, "ReceivedItems", "receivedItems", "ReceivedCount");
                                var receivedItems = ParseInt(receivedItemsStr);

                                var totalAmountStr = GetStringProp(b, "TotalAmount", "totalAmount", "Total", "total", "TotalValue", "totalValue", "ราคารวม");
                                var totalAmount = ParseDouble(totalAmountStr);

                                var isReceived = b.TryGetProperty("IsReceived", out var ir) && (ir.ValueKind == JsonValueKind.True || (ir.ValueKind == JsonValueKind.String && ir.GetString()?.ToLower() == "true"));
                                var receiveDateStr = GetStringProp(b, "ReceiveDate", "receiveDate");
                                if (!isReceived && totalItems > 0 && receivedItems >= totalItems)
                                {
                                    isReceived = true;
                                }

                                DateTime.TryParse(createdAtStr, out var createdAt);

                                if (year.HasValue && year.Value > 0 && createdAt.Year != year.Value) continue;
                                if (month.HasValue && month.Value > 0 && createdAt.Month != month.Value) continue;

                                rawList.Add(new TrackingBatchViewModel
                                {
                                    BatchName = name,
                                    CreatedAt = createdAt,
                                    TotalItems = totalItems,
                                    ReceivedItems = receivedItems,
                                    TotalAmount = totalAmount,
                                    IsReceived = isReceived,
                                    ReceiveDate = receiveDateStr
                                });

                                if (!availableYears.Contains(createdAt.Year))
                                    availableYears.Add(createdAt.Year);
                            }

                            foreach (var b in rawList)
                            {
                                if (b.TotalAmount == 0 && !string.IsNullOrWhiteSpace(b.BatchName))
                                {
                                    try
                                    {
                                        var detailRes = await client.GetAsync($"{appScriptUrl}?action=getBatchDetails&batchName={Uri.EscapeDataString(b.BatchName)}");
                                        if (detailRes.IsSuccessStatusCode)
                                        {
                                            var dJson = await detailRes.Content.ReadAsStringAsync();
                                            using var dDoc = JsonDocument.Parse(dJson);
                                            var dRoot = dDoc.RootElement;
                                            if (dRoot.TryGetProperty("items", out var dItemsEl) && dItemsEl.ValueKind == JsonValueKind.Array)
                                            {
                                                double calcSum = 0;
                                                foreach (var it in dItemsEl.EnumerateArray())
                                                {
                                                    var pCode = GetStringProp(it, "ProductCode", "productCode", "Code", "code", "รหัสสินค้า");
                                                    var qty = ParseDouble(GetStringProp(it, "Quantity", "quantity", "Qty", "qty", "จำนวน"));
                                                    var uPrice = ParseDouble(GetStringProp(it, "UnitPrice", "unitPrice", "Price", "price", "PricePerUnit", "Cost", "cost", "ราคาต่อหน่วย", "ราคา/หน่วย"));
                                                    var tAmt = ParseDouble(GetStringProp(it, "TotalAmount", "totalAmount", "Total", "total", "ราคารวม", "มูลค่ารวม"));

                                                    if (uPrice == 0 && qty > 0 && tAmt > 0) uPrice = tAmt / qty;
                                                    if (uPrice == 0 && !string.IsNullOrWhiteSpace(pCode) && priceDict.TryGetValue(pCode, out var dbP)) uPrice = dbP;

                                                    calcSum += (tAmt > 0 ? tAmt : (qty * uPrice));
                                                }
                                                b.TotalAmount = calcSum;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                batches.Add(b);
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
                                    ProductCode = GetStringProp(it, "ProductCode", "productCode", "Code", "code", "รหัสสินค้า"),
                                    ProductName = GetStringProp(it, "ProductName", "productName", "Name", "name", "ชื่อสินค้า", "ชื่อสินค้า / รายการอะไหล่"),
                                    Quantity = GetStringProp(it, "Quantity", "quantity", "Qty", "qty", "จำนวน"),
                                    Unit = GetStringProp(it, "Unit", "unit", "หน่วย"),
                                    IsReceived = it.TryGetProperty("IsReceived", out var ir) && (ir.ValueKind == JsonValueKind.True || (ir.ValueKind == JsonValueKind.String && ir.GetString()?.ToLower() == "true")),
                                    ReceiveDate = GetStringProp(it, "ReceiveDate", "receiveDate")
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
