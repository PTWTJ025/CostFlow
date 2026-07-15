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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public ProductSearchController(AppDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task<IActionResult> Index(string? editBatchName = null)
        {
            ViewData["SpreadsheetUrl"] = _configuration["GoogleSheets:SpreadsheetUrl"] ?? "https://docs.google.com/spreadsheets/d/1DJeeOYd1hGFAaRZ7emkdgLys7G88T13cylayh6Za1xc/edit";
            if (!string.IsNullOrWhiteSpace(editBatchName))
            {
                // Fetch batch data from Google Apps Script
                string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
                if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                {
                    try
                    {
                        var client = _httpClientFactory.CreateClient("GoogleAppsScript");
                        client.Timeout = TimeSpan.FromSeconds(30);
                        var response = await client.GetAsync(appScriptUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("batches", out var batchesEl))
                            {
                                foreach (var b in batchesEl.EnumerateArray())
                                {
                                    var name = b.TryGetProperty("BatchName", out var nameProp) ? nameProp.GetString() : null;
                                    if (name == editBatchName && b.TryGetProperty("Orders", out var ordersEl))
                                    {
                                        ViewData["EditBatchName"] = editBatchName;
                                        ViewData["EditOrdersJson"] = ordersEl.GetRawText();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* silently ignore; user can re-enter data */ }
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
            if (request == null || request.Orders == null || !request.Orders.Any())
            {
                return Json(new { success = false, error = "ไม่มีข้อมูลใบสั่งซื้อที่จะบันทึก" });
            }

            try
            {
                var now = DateTime.Now;
                var batchName = string.IsNullOrWhiteSpace(request.BatchName)
                    ? $"รายการคีย์ข้อมูลวันที่ {now.ToString("dd/MM/yyyy HH:mm")}"
                    : request.BatchName.Trim();

                // --- Send directly to Google Sheets (no DB) ---
                string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
                if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
                {
                    return Json(new { success = false, error = "ยังไม่ได้ระบุลิงก์ Google Sheets Web App ในไฟล์ตั้งค่าระบบ" });
                }

                var payload = new
                {
                    BatchName = batchName,
                    Orders = request.Orders.Select(o => new
                    {
                        ProductCode = o.ProductCode ?? string.Empty,
                        ProductName = o.ProductName ?? string.Empty,
                        Unit = o.Unit ?? string.Empty,
                        UnitPrice = ParseDecimal(o.UnitPrice),
                        Quantity = ParseDecimal(o.Quantity),
                        Remarks = o.Remarks ?? string.Empty
                    }).ToList()
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
                return Json(new { success = false, error = $"เกิดข้อผิดพลาดในการบันทึกข้อมูล: {ex.Message}" });
            }
        }

        // POST: /ProductSearch/DeleteBatch
        [HttpPost]
        public async Task<IActionResult> DeleteBatch(string batchName)
        {
            if (string.IsNullOrWhiteSpace(batchName))
                return Json(new { success = false, error = "ไม่ระบุชื่อแผ่นงานที่จะลบ" });

            string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
            if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
                return Json(new { success = false, error = "ยังไม่ได้ตั้งค่า Google Sheets Web App URL" });

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var payload = new { Action = "delete", BatchName = batchName };
                var jsonString = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(appScriptUrl, content);
                if (!response.IsSuccessStatusCode)
                    return Json(new { success = false, error = $"Google Sheets ตอบกลับ HTTP {(int)response.StatusCode}" });

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        // GET: /ProductSearch/SavedOrders
        [HttpGet]
        public async Task<IActionResult> SavedOrders(int? year, int? month)
        {
            var batches = new List<SavedBatchViewModel>();
            var availableYears = new List<int> { DateTime.Now.Year };

            string? appScriptUrl = _configuration["GoogleSheets:OrderHistoryAppScriptUrl"];
            if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
            {
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var response = await client.GetAsync(appScriptUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("batches", out var batchesEl))
                        {
                            foreach (var b in batchesEl.EnumerateArray())
                            {
                                var batchName = b.TryGetProperty("BatchName", out var np) ? np.GetString() ?? "" : "";
                                var createdAtStr = b.TryGetProperty("CreatedAt", out var cp) ? cp.GetString() ?? "" : "";
                                var totalItems = b.TryGetProperty("TotalItems", out var tip) ? tip.GetInt32() : 0;
                                var totalAmount = b.TryGetProperty("TotalAmount", out var tap) ? tap.GetDouble() : 0;

                                DateTime.TryParse(createdAtStr, out var createdAt);

                                // Filter by year/month if requested
                                if (year.HasValue && year.Value > 0 && createdAt.Year != year.Value) continue;
                                if (month.HasValue && month.Value > 0 && createdAt.Month != month.Value) continue;

                                batches.Add(new SavedBatchViewModel
                                {
                                    BatchName = batchName,
                                    CreatedAt = createdAt,
                                    TotalItems = totalItems,
                                    TotalAmount = totalAmount
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
            ViewData["AvailableYears"] = availableYears;

            return View(batches);
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
        public string BatchName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int TotalItems { get; set; }
        public double TotalAmount { get; set; }
    }
}
