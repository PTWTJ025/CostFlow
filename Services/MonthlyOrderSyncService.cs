using CostFlow.Data;
using CostFlow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace CostFlow.Services;

/// <summary>
/// Service สำหรับจัดการสำรองและ Sync ข้อมูล MonthlyOrderActions, SavedOrderBatches, และ Received Items ไป Google Sheets แบบ Manual Trigger
/// </summary>
public class MonthlyOrderSyncService
{
    private readonly AppDbContext _context;
    private readonly TiDbContext _tiContext;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public MonthlyOrderSyncService(
        AppDbContext context,
        TiDbContext tiContext,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _tiContext = tiContext;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Auto Sync ถูกปิดการใช้งาน - บันทึกลง Database อย่างเดียว
    /// </summary>
    public Task SyncSpecificMonthsToGoogleSheetsAsync(List<string> monthKeys, string reason = "User Save Action")
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// ฟังก์ชัน Manual Trigger สำหรับ Admin กดส่งออก/สำรองข้อมูลทุกเดือนทั้งหมดใน Database ไปยัง Google Sheets
    /// </summary>
    public async Task<(bool Success, string Message, int TotalActions, int TotalMonths)> SyncAllMonthsToGoogleSheetsAsync(string reason = "Manual Admin Sync")
    {
        try
        {
            string? appScriptUrl = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
            if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
            {
                appScriptUrl = _configuration["GoogleSheets:ArchiveAppScriptUrl"];
            }

            if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
            {
                return (false, "ยังไม่ได้ตั้งค่า Google Sheets Web App URL ในไฟล์ตั้งค่าระบบ (appsettings.json)", 0, 0);
            }

            // ทำความสะอาด MonthYear ใน DB ก่อน query
            try
            {
                await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET MonthYear = SUBSTR(MonthYear, 1, 7) WHERE LENGTH(MonthYear) > 7;");
                await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET DeferredFromMonth = SUBSTR(DeferredFromMonth, 1, 7) WHERE DeferredFromMonth IS NOT NULL AND LENGTH(DeferredFromMonth) > 7;");
            }
            catch { }

            // ดึงข้อมูล actions ทั้งหมด
            var rawActions = await _context.MonthlyOrderActions
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.Report)
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .OrderBy(a => a.MonthYear)
                .ThenBy(a => a.CreatedAt)
                .ToListAsync();

            if (!rawActions.Any())
            {
                return (true, "ไม่มีข้อมูลรายการค่าใช้จ่ายในฐานข้อมูลสำหรับส่งออก", 0, 0);
            }

            // Deduplicate: 1 Order ต่อ 1 Month เท่านั้น (เอาตัวล่าสุด) พร้อม Normalize MonthYear Key ให้ถูกต้อง
            var specificActions = rawActions
                .Select(a => {
                    var normKey = NormalizeMonthYearKey(a.MonthYear, a.CreatedAt);
                    a.MonthYear = normKey;
                    return a;
                })
                .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear))
                .Select(g => g.Last())
                .OrderBy(a => a.MonthYear)
                .ThenBy(a => a.CreatedAt)
                .ToList();

            var allPriorActions = specificActions;

            var formattedActionItems = new List<(string MonthYear, object?[] Row)>();

            foreach (var act in specificActions)
            {
                var otm = act.OrderTrackingMaster;
                if (otm == null) continue;

                var latestPlan = otm.MatchedInWeeklyPlans
                    .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                    .FirstOrDefault();

                string poNo = otm.PoNumber ?? "-";
                string orderName = !string.IsNullOrWhiteSpace(otm.Remarks) ? otm.Remarks.Replace("สั่งทำ ", "").Replace("สั่งทำ", "").Trim() : "ไม่ระบุ";
                string dept = latestPlan?.Department ?? otm.Urgency ?? "-";

                string approvedDateDisplay = NormalizeApprovedDate(otm.ApprovedDate);

                decimal itemTotalPrice = 0m;
                if (!string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var parsedAmt))
                {
                    itemTotalPrice = parsedAmt;
                }

                decimal displayAmount = (act.Action == "Deferred" || act.Action == "ReceivedFull")
                    ? (act.ActionPrice > 0 ? act.ActionPrice : itemTotalPrice)
                    : itemTotalPrice;

                string statusLabel = act.Action switch
                {
                    "ReceivedFull" => "รับสินค้าแล้ว",
                    "Deferred" => "ผ่อนชำระ",
                    "Skipped" => "ยังไม่รับสินค้า",
                    _ => act.Action
                };

                var actMonth = act.MonthYear;
                var priorAct = allPriorActions
                    .Where(x => x.OrderTrackingMasterId == otm.Id && string.Compare(x.MonthYear, actMonth) < 0)
                    .OrderByDescending(x => x.MonthYear)
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefault();

                string carriedOverFromMonth = "-";
                if (priorAct != null && (priorAct.Action == "Deferred" || priorAct.Action == "Skipped"))
                {
                    carriedOverFromMonth = ConvertKeyToThaiMonth(priorAct.MonthYear);
                }

                string qtyDisplay = FormatQuantityDisplay(otm.RemarksQuantity, otm.Remarks);
                var normalizedMonthYear = act.MonthYear;

                formattedActionItems.Add((normalizedMonthYear, new object?[]
                {
                    act.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    approvedDateDisplay,
                    poNo,
                    orderName,
                    qtyDisplay,
                    dept,
                    displayAmount,
                    statusLabel,
                    carriedOverFromMonth
                }));
            }

            var actionGroups = formattedActionItems
                .GroupBy(item => item.MonthYear)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    MonthYear = g.Key,
                    Rows = g.Select(x => x.Row).ToList()
                }).ToList();

            // ─── 2. ดึงข้อมูลจาก TiDB: ประวัติการสั่งซื้อ & ติดตามการรับสินค้า ───
            var orderHistoryRows = new List<object?[]>();
            var receivedTrackingItems = new List<(string MonthYear, object?[] Row)>();

            try
            {
                var allBatches = await _tiContext.SavedOrderBatches
                    .Include(b => b.Items)
                    .OrderByDescending(b => b.CreatedAt)
                    .ToListAsync();

                foreach (var batch in allBatches)
                {
                    string batchDateDisplay = batch.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    string bName = batch.BatchName ?? "-";

                    foreach (var itm in batch.Items)
                    {
                        decimal unitP = itm.UnitPrice;
                        decimal qty = itm.Quantity;
                        decimal totalP = unitP * qty;

                        // แท็บ ประวัติการสั่งซื้อ
                        orderHistoryRows.Add(new object?[]
                        {
                            batchDateDisplay,
                            bName,
                            itm.ProductCode ?? "-",
                            itm.ProductName ?? "-",
                            itm.Unit ?? "-",
                            unitP,
                            qty,
                            totalP,
                            itm.Remarks ?? "-"
                        });

                        // แท็บ ติดตามการรับสินค้า (เฉพาะรายการที่กดรับของแล้ว)
                        if (itm.IsReceived)
                        {
                            DateTime recDate = itm.ReceiveDate ?? batch.CreatedAt;
                            string recMonthKey = NormalizeMonthYearKey(recDate.ToString("yyyy-MM"), recDate);
                            string recDateDisplay = recDate.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

                            receivedTrackingItems.Add((recMonthKey, new object?[]
                            {
                                itm.ProductCode ?? "-",
                                itm.ProductName ?? "-",
                                $"{qty} {itm.Unit}".Trim(),
                                "รับสินค้าแล้ว",
                                "Admin",
                                recDateDisplay
                            }));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch TiDB order batches: {ex.Message}");
            }

            var receivedTrackingGroups = receivedTrackingItems
                .GroupBy(item => item.MonthYear)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    MonthYear = g.Key,
                    Rows = g.Select(x => x.Row).ToList()
                }).ToList();

            var sheetPayload = new
            {
                SheetName_Actions = "บันทึกการรับของประจำเดือน",
                ActionGroups = actionGroups,
                SheetName_OrderHistory = "ประวัติการสั่งซื้อ",
                OrderHistoryRows = orderHistoryRows,
                SheetName_ReceivedTracking = "ติดตามการรับสินค้า",
                ReceivedTrackingGroups = receivedTrackingGroups
            };

            var client = _httpClientFactory.CreateClient("GoogleAppsScript");
            client.Timeout = TimeSpan.FromSeconds(120);

            var jsonString = JsonSerializer.Serialize(sheetPayload);
            var content = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync(appScriptUrl, content);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"ส่งข้อมูลลง Google Sheet สำเร็จ ครบทั้ง 3 แท็บ (บันทึกรายเดือน {formattedActionItems.Count} รายการ, ประวัติสั่งซื้อ {orderHistoryRows.Count} รายการ, ติดตามรับของ {receivedTrackingItems.Count} รายการ)", formattedActionItems.Count, actionGroups.Count);
            }
            else
            {
                var errBody = await response.Content.ReadAsStringAsync();
                return (false, $"Google Sheets ตอบกลับ HTTP {(int)response.StatusCode}: {errBody}", 0, 0);
            }
        }
        catch (Exception ex)
        {
            return (false, $"เกิดข้อผิดพลาดในการเชื่อมต่อ Google Sheets: {ex.Message}", 0, 0);
        }
    }

    private static string ConvertKeyToThaiMonth(string monthKey)
    {
        if (string.IsNullOrWhiteSpace(monthKey) || !monthKey.Contains("-")) return monthKey ?? string.Empty;

        monthKey = monthKey.Trim();
        var parts = monthKey.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var year)) return monthKey;

        var monthStr = parts[1].Trim();
        if (monthStr.Length > 2)
        {
            monthStr = monthStr.Substring(0, 2);
        }
        if (!int.TryParse(monthStr, out var month)) return monthKey;

        var thaiMonths = new[] {
            "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
            "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
        };

        if (month < 1 || month > 12) return monthKey;

        var thaiYear = year + 543;
        return $"{thaiMonths[month - 1]} {thaiYear}";
    }

    private static string FormatQuantityDisplay(string? qtyStr, string? remarks)
    {
        if (string.IsNullOrWhiteSpace(qtyStr)) return "-";
        var qty = qtyStr.Trim();
        if (qty.EndsWith("ชิ้น")) return qty;
        return $"{qty} ชิ้น";
    }

    private static string NormalizeApprovedDate(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate) || rawDate == "-") return "-";
        rawDate = rawDate.Trim();

        if (rawDate.Length >= 10 && rawDate[4] == '-' && rawDate[7] == '-')
        {
            if (DateTime.TryParse(rawDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var isoDate))
            {
                return isoDate.ToString("dd/MM/yyyy HH:mm");
            }
        }

        if (rawDate.Contains('/'))
        {
            var datePart = rawDate.Split(' ')[0];
            var parts = datePart.Split('/');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var d) &&
                int.TryParse(parts[1], out var m) &&
                int.TryParse(parts[2], out var y))
            {
                if (y > 2500) y -= 543;
                try
                {
                    var thaiDate = new DateTime(y, m, d);
                    var timePart = rawDate.Contains(' ') ? rawDate.Substring(rawDate.IndexOf(' ') + 1) : "";
                    if (!string.IsNullOrEmpty(timePart) &&
                        TimeSpan.TryParse(timePart, out var ts))
                    {
                        thaiDate = thaiDate.Add(ts);
                    }
                    return thaiDate.ToString("dd/MM/yyyy HH:mm");
                }
                catch { }
            }
        }

        return rawDate;
    }

    private static string NormalizeMonthYearKey(string? rawKey, DateTime fallbackDate)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return fallbackDate.ToString("yyyy-MM");

        rawKey = rawKey.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(rawKey, @"^(\d{4})-(\d{1,2})");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int y) && int.TryParse(match.Groups[2].Value, out int m))
        {
            if (m >= 1 && m <= 12)
            {
                return $"{y:0000}-{m:00}";
            }
            // If malformed by substring like 22 -> 2, 32 -> 3, 42 -> 4, etc.
            int tens = m / 10;
            if (tens >= 1 && tens <= 12)
            {
                return $"{y:0000}-{tens:00}";
            }
        }
        return fallbackDate.ToString("yyyy-MM");
    }
}
