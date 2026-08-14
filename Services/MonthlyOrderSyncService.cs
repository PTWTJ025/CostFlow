using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CostFlow.Data;
using CostFlow.Models;
using System.Globalization;

namespace CostFlow.Services;

/// <summary>
/// Service สำหรับ Sync ข้อมูล MonthlyOrderActions ไป Google Sheets
/// แยกออกมาเพื่อให้ทั้ง Controller และ Background Service ใช้งานได้
/// </summary>
public class MonthlyOrderSyncService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public MonthlyOrderSyncService(
        AppDbContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// ส่งข้อมูลเฉพาะเดือนที่ระบุไป Google Sheets พร้อมระบุสาเหตุ
    /// </summary>
    public async Task SyncSpecificMonthsToGoogleSheetsAsync(List<string> monthKeys, string reason = "User Save Action")
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
                Console.WriteLine($"⚠️ [GOOGLE SHEETS SYNC] Google Sheets URL ไม่ได้ตั้งค่า - ข้ามการ sync (สาเหตุ: {reason})");
                return;
            }

            // ทำความสะอาด MonthYear ใน DB ก่อน query (กรณียังไม่ได้ restart)
            try
            {
                await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET MonthYear = SUBSTR(MonthYear, 1, 7) WHERE LENGTH(MonthYear) > 7;");
                await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET DeferredFromMonth = SUBSTR(DeferredFromMonth, 1, 7) WHERE DeferredFromMonth IS NOT NULL AND LENGTH(DeferredFromMonth) > 7;");
            }
            catch { }

            // Normalize monthKeys ให้เหลือแค่ 7 ตัวอักษรก่อน query
            var normalizedKeys = monthKeys
                .Select(k => k.Length > 7 ? k.Substring(0, 7) : k)
                .Distinct()
                .ToList();

            Console.WriteLine("\n┌──────────────────────────────────────────────────────────────────");
            Console.WriteLine($"│ 📡 [GOOGLE SHEETS SYNC START]");
            Console.WriteLine($"│ ⏰ เวลา: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"│ 📌 สาเหตุที่ส่ง: {reason}");
            Console.WriteLine($"│ 📅 เดือนเป้าหมาย ({normalizedKeys.Count} เดือน): [{string.Join(", ", normalizedKeys)}]");
            Console.WriteLine("└──────────────────────────────────────────────────────────────────");

            // ดึงเฉพาะ actions ของเดือนที่ระบุ (normalizedKeys)
            var rawActions = await _context.MonthlyOrderActions
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.Report)
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .Where(a => normalizedKeys.Contains(a.MonthYear.Length > 7 ? a.MonthYear.Substring(0, 7) : a.MonthYear))
                .OrderBy(a => a.MonthYear)
                .ThenByDescending(a => a.CreatedAt)
                .ToListAsync();

            if (!rawActions.Any())
            {
                Console.WriteLine($"ℹ️ [GOOGLE SHEETS SYNC] ไม่มีข้อมูลที่ต้อง sync สำหรับเดือน [{string.Join(", ", normalizedKeys)}]\n");
                return;
            }

            // Deduplicate: 1 Order ต่อ 1 Month เท่านั้น (เอาตัวล่าสุด)
            var specificActions = rawActions
                .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear.Length > 7 ? a.MonthYear.Substring(0, 7) : a.MonthYear))
                .Select(g => g.First())
                .OrderBy(a => a.MonthYear)
                .ThenBy(a => a.CreatedAt)
                .ToList();

            var formattedActionItems = new List<(string MonthYear, object?[] Row)>();

            // ดึงข้อมูล prior actions ทั้งหมดที่เกิดขึ้นก่อนเดือนสูงสุดใน batch (maxMonthKey)
            // เพื่อให้แต่ละเดือนใน batch สามารถหา prior action ของเดือนก่อนหน้าตัวเองได้ถูกต้อง (แก้บั๊ก sync หลายเดือนพร้อมกันแล้วยกยอดข้ามเดือน)
            var maxMonthKey = normalizedKeys.Max();
            var allPriorActions = await _context.MonthlyOrderActions
                .Where(a => string.Compare(a.MonthYear, maxMonthKey) < 0)
                .OrderByDescending(a => a.MonthYear)
                .ThenByDescending(a => a.CreatedAt)
                .ToListAsync();

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
                
                // Normalize ApprovedDate ให้เป็น dd/MM/yyyy HH:mm เสมอ
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

                // หา prior action ของ order นี้ในเดือนก่อนหน้าเทียบกับ act.MonthYear ของแถวปัจจุบัน
                var actMonth = act.MonthYear.Length > 7 ? act.MonthYear.Substring(0, 7) : act.MonthYear;
                var priorAct = allPriorActions
                    .Where(x => x.OrderTrackingMasterId == otm.Id && string.Compare(x.MonthYear.Length > 7 ? x.MonthYear.Substring(0, 7) : x.MonthYear, actMonth) < 0)
                    .OrderByDescending(x => x.MonthYear)
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefault();
                
                // ยกยอดมาจาก - รวมทั้ง Deferred และ Skipped
                string carriedOverFromMonth = "-";
                
                if (priorAct != null && (priorAct.Action == "Deferred" || priorAct.Action == "Skipped"))
                {
                    carriedOverFromMonth = ConvertKeyToThaiMonth(priorAct.MonthYear);
                }

                string qtyDisplay = FormatQuantityDisplay(otm.RemarksQuantity, otm.Remarks);

                // ใช้ normalized MonthYear (7 ตัวอักษร) เป็น key ของ group เสมอ
                var normalizedMonthYear = act.MonthYear.Length > 7 ? act.MonthYear.Substring(0, 7) : act.MonthYear;

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
                    carriedOverFromMonth  // คอลัมน์ที่ 9: ยกยอดมาจาก (รวมทั้ง Deferred และ Skipped)
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

            var sheetPayload = new
            {
                SheetName_Actions = "บันทึกการรับของประจำเดือน",
                ActionGroups = actionGroups
            };

            var client = _httpClientFactory.CreateClient("GoogleAppsScript");
            client.Timeout = TimeSpan.FromSeconds(30);

            var jsonString = System.Text.Json.JsonSerializer.Serialize(sheetPayload);
            var content = new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

            Console.WriteLine($"🚀 กำลังส่ง Payload ไปยัง Google Sheets ({actionGroups.Sum(g => g.Rows.Count)} แถว รวม {actionGroups.Count} บล็อกเดือน)...");
            foreach (var grp in actionGroups)
            {
                Console.WriteLine($"   ↳ บล็อกเดือน {grp.MonthYear}: {grp.Rows.Count} รายการ");
            }

            var response = await client.PostAsync(appScriptUrl, content);
            
            if (response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"✅ [GOOGLE SHEETS SYNC SUCCESS] สำเร็จ ({response.StatusCode}) | สาเหตุ: {reason}");
                Console.WriteLine($"   ข้อความตอบกลับ: {respBody}\n");
            }
            else
            {
                var errBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ [GOOGLE SHEETS SYNC FAILED] ล้มเหลว ({response.StatusCode}) | สาเหตุ: {reason} | ข้อผิดพลาด: {errBody}\n");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [GOOGLE SHEETS SYNC ERROR] สาเหตุ: {reason} | Error: {ex.Message}\n");
            throw;
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

    /// <summary>
    /// Normalize ApprovedDate ให้เป็น dd/MM/yyyy HH:mm เสมอ
    /// รองรับทั้ง ISO format ("2025-11-21 15:49:13") และ Thai format ("21/11/2025 15:49:13")
    /// </summary>
    private static string NormalizeApprovedDate(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate) || rawDate == "-") return "-";

        rawDate = rawDate.Trim();

        // ลอง parse ISO format: yyyy-MM-dd HH:mm:ss หรือ yyyy-MM-ddTHH:mm:ss
        if (rawDate.Length >= 10 && rawDate[4] == '-' && rawDate[7] == '-')
        {
            if (DateTime.TryParse(rawDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var isoDate))
            {
                return isoDate.ToString("dd/MM/yyyy HH:mm");
            }
        }

        // ลอง parse Thai format: dd/MM/yyyy หรือ dd/MM/yyyy HH:mm:ss
        if (rawDate.Contains('/'))
        {
            var datePart = rawDate.Split(' ')[0];
            var parts = datePart.Split('/');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var d) &&
                int.TryParse(parts[1], out var m) &&
                int.TryParse(parts[2], out var y))
            {
                if (y > 2500) y -= 543; // Buddhist → Gregorian
                try
                {
                    var thaiDate = new DateTime(y, m, d);
                    // หาส่วนเวลาถ้ามี
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

        // Fallback: คืนค่าเดิม
        return rawDate;
    }
}
