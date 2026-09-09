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
    private readonly IDateTimeProvider _dateTimeProvider;

    public MonthlyOrderSyncService(
        AppDbContext context,
        TiDbContext tiContext,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDateTimeProvider dateTimeProvider)
    {
        _context = context;
        _tiContext = tiContext;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _dateTimeProvider = dateTimeProvider;
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
    public async Task<(bool Success, string Message, int TotalActions, int TotalMonths, object? Details)> SyncAllMonthsToGoogleSheetsAsync(string reason = "Manual Admin Sync")
    {
        try
        {
            string? appScriptUrlPrimary = _configuration["GoogleSheets:PrimarySyncAppScriptUrl"];
            if (string.IsNullOrWhiteSpace(appScriptUrlPrimary) || appScriptUrlPrimary.Contains("_placeholder"))
            {
                appScriptUrlPrimary = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
            }
            if (string.IsNullOrWhiteSpace(appScriptUrlPrimary) || appScriptUrlPrimary.Contains("_placeholder"))
            {
                appScriptUrlPrimary = _configuration["GoogleSheets:ArchiveAppScriptUrl"];
            }

            string? appScriptUrlSecondary = _configuration["GoogleSheets:SecondarySyncAppScriptUrl"];

            if (string.IsNullOrWhiteSpace(appScriptUrlPrimary) || appScriptUrlPrimary.Contains("_placeholder"))
            {
                return (false, "ยังไม่ได้ตั้งค่า Google Sheets Web App URL ในไฟล์ตั้งค่าระบบ (appsettings.json)", 0, 0, null);
            }

            // ทำความสะอาด MonthYear ใน DB ก่อน query
            try
            {
                await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET MonthYear = SUBSTR(MonthYear, 1, 7) WHERE LENGTH(MonthYear) > 7;");
                await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET DeferredFromMonth = SUBSTR(DeferredFromMonth, 1, 7) WHERE DeferredFromMonth IS NOT NULL AND LENGTH(DeferredFromMonth) > 7;");
            }
            catch { }

            // =========================================================================
            // 📂 ก้อนที่ 1: เตรียมข้อมูลสำหรับ Google Sheet 1 (ข้อมูลหลัก / Core Operations)
            // =========================================================================

            // 1.1 บันทึกการรับของประจำเดือน (MonthlyOrderActions พร้อมระบบ Rolling Backlog เหมือนหน้า Summary)
            var now = _dateTimeProvider.Now;
            var currentMonthKey = $"{now.Year:0000}-{now.Month:00}";
            var currentMonthDate = new DateTime(now.Year, now.Month, 1);

            // ดึงคำสั่งซื้อและ Action ทั้งหมด
            var allOrders = await _context.OrderTrackingMasters
                .Include(o => o.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .Include(o => o.Report)
                .ToListAsync();

            var rawActions = await _context.MonthlyOrderActions
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.Report)
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .OrderBy(a => a.MonthYear)
                .ThenBy(a => a.CreatedAt)
                .ToListAsync();

            var actionsByOrder = rawActions
                .Select(a => {
                    a.MonthYear = NormalizeMonthYearKey(a.MonthYear, a.CreatedAt);
                    return a;
                })
                .GroupBy(a => a.OrderTrackingMasterId)
                .ToDictionary(g => g.Key, g => g.ToList());

            static DateTime ParseMonthYearHelper(string my)
            {
                if (string.IsNullOrWhiteSpace(my)) return DateTime.MinValue;
                var parts = my.Split('-');
                if (parts.Length >= 2 && int.TryParse(parts[0], out var y) && int.TryParse(parts[1], out var m))
                {
                    if (m >= 1 && m <= 12) return new DateTime(y, m, 1);
                }
                return DateTime.MinValue;
            }

            var formattedActionItems = new List<(string MonthYear, object?[] Row)>();

            // ── A. ข้อมูลประวัติเดือนที่ผ่านมา (Historical Months < currentMonthKey) ──
            var historicalActions = rawActions
                .Where(a => string.Compare(a.MonthYear, currentMonthKey, StringComparison.Ordinal) < 0)
                .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear))
                .Select(g => g.Last())
                .ToList();

            foreach (var act in historicalActions)
            {
                var otm = act.OrderTrackingMaster ?? allOrders.FirstOrDefault(o => o.Id == act.OrderTrackingMasterId);
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

                string carriedOverFromMonth = "-";
                var orderActs = actionsByOrder.ContainsKey(otm.Id) ? actionsByOrder[otm.Id] : new List<MonthlyOrderAction>();
                var priorAct = orderActs
                    .Where(x => string.Compare(x.MonthYear, act.MonthYear, StringComparison.Ordinal) < 0)
                    .OrderByDescending(x => ParseMonthYearHelper(x.MonthYear))
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefault();

                if (priorAct != null && (priorAct.Action == "Deferred" || priorAct.Action == "Skipped"))
                {
                    carriedOverFromMonth = (priorAct.Action == "Deferred" ? "ผ่อนชำระมาจาก " : "ค้างรับมาจาก ") + ConvertKeyToThaiMonth(priorAct.MonthYear);
                }

                string qtyDisplay = FormatQuantityDisplay(otm.RemarksQuantity, otm.Remarks);

                formattedActionItems.Add((act.MonthYear, new object?[]
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

            // ── B. เดือนปัจจุบัน (Current Month Block) & Future Deferrals (Rolling Backlog แบบหน้า Summary) ──
            var processedCurrentMonthOrderIds = new HashSet<Guid>();

            foreach (var otm in allOrders)
            {
                var orderActs = actionsByOrder.ContainsKey(otm.Id) ? actionsByOrder[otm.Id] : new List<MonthlyOrderAction>();
                var latestAct = orderActs
                    .OrderByDescending(a => ParseMonthYearHelper(a.MonthYear))
                    .ThenByDescending(a => a.CreatedAt)
                    .FirstOrDefault();

                var approvedDt = ParseThaiDate(otm.ApprovedDate);
                var latestPlan = otm.MatchedInWeeklyPlans
                    .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                    .FirstOrDefault();

                string poNo = otm.PoNumber ?? "-";
                string orderName = !string.IsNullOrWhiteSpace(otm.Remarks) ? otm.Remarks.Replace("สั่งทำ ", "").Replace("สั่งทำ", "").Trim() : "ไม่ระบุ";
                string dept = latestPlan?.Department ?? otm.Urgency ?? "-";
                string approvedDateDisplay = NormalizeApprovedDate(otm.ApprovedDate);
                string qtyDisplay = FormatQuantityDisplay(otm.RemarksQuantity, otm.Remarks);

                decimal itemTotalPrice = 0m;
                if (!string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var parsedAmt))
                {
                    itemTotalPrice = parsedAmt;
                }

                var currentMonthAct = orderActs.FirstOrDefault(a => a.MonthYear == currentMonthKey);

                if (currentMonthAct != null)
                {
                    decimal displayAmount = (currentMonthAct.Action == "Deferred" || currentMonthAct.Action == "ReceivedFull")
                        ? (currentMonthAct.ActionPrice > 0 ? currentMonthAct.ActionPrice : itemTotalPrice)
                        : itemTotalPrice;

                    string statusLabel = currentMonthAct.Action switch
                    {
                        "ReceivedFull" => "รับสินค้าแล้ว",
                        "Deferred" => "ผ่อนชำระ",
                        "Skipped" => "ยังไม่รับสินค้า",
                        _ => currentMonthAct.Action
                    };

                    string carriedOver = "-";
                    var priorAct = orderActs
                        .Where(x => string.Compare(x.MonthYear, currentMonthKey, StringComparison.Ordinal) < 0)
                        .OrderByDescending(x => ParseMonthYearHelper(x.MonthYear))
                        .ThenByDescending(x => x.CreatedAt)
                        .FirstOrDefault();

                    if (priorAct != null)
                    {
                        carriedOver = (priorAct.Action == "Deferred" ? "ผ่อนชำระมาจาก " : "ค้างรับมาจาก ") + ConvertKeyToThaiMonth(priorAct.MonthYear);
                    }
                    else if (approvedDt.HasValue && $"{approvedDt.Value.Year:0000}-{approvedDt.Value.Month:00}" != currentMonthKey)
                    {
                        carriedOver = "ค้างรับมาจาก " + ConvertKeyToThaiMonth($"{approvedDt.Value.Year:0000}-{approvedDt.Value.Month:00}");
                    }

                    formattedActionItems.Add((currentMonthKey, new object?[]
                    {
                        currentMonthAct.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                        approvedDateDisplay,
                        poNo,
                        orderName,
                        qtyDisplay,
                        dept,
                        displayAmount,
                        statusLabel,
                        carriedOver
                    }));
                    processedCurrentMonthOrderIds.Add(otm.Id);
                }
                else
                {
                    if (latestAct != null && latestAct.Action == "ReceivedFull")
                    {
                        continue;
                    }

                    if (latestAct != null && latestAct.Action == "Deferred")
                    {
                        var lKey = latestAct.MonthYear;
                        var parts = lKey.Split('-');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out var dy) && int.TryParse(parts[1], out var dm))
                        {
                            var targetDate = new DateTime(dy, dm, 1).AddMonths(1);
                            var targetKey = $"{targetDate.Year:0000}-{targetDate.Month:00}";

                            if (targetDate == currentMonthDate)
                            {
                                decimal displayAmount = latestAct.ActionPrice > 0 ? latestAct.ActionPrice : itemTotalPrice;
                                string carriedOver = "ผ่อนชำระมาจาก " + ConvertKeyToThaiMonth(lKey);

                                formattedActionItems.Add((currentMonthKey, new object?[]
                                {
                                    latestAct.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                                    approvedDateDisplay,
                                    poNo,
                                    orderName,
                                    qtyDisplay,
                                    dept,
                                    displayAmount,
                                    "ผ่อนชำระ",
                                    carriedOver
                                }));
                                processedCurrentMonthOrderIds.Add(otm.Id);
                                continue;
                            }
                            else if (targetDate > currentMonthDate)
                            {
                                decimal displayAmount = latestAct.ActionPrice > 0 ? latestAct.ActionPrice : itemTotalPrice;
                                string carriedOver = "ผ่อนชำระมาจาก " + ConvertKeyToThaiMonth(lKey);

                                formattedActionItems.Add((targetKey, new object?[]
                                {
                                    latestAct.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                                    approvedDateDisplay,
                                    poNo,
                                    orderName,
                                    qtyDisplay,
                                    dept,
                                    displayAmount,
                                    "ผ่อนชำระ",
                                    carriedOver
                                }));
                                continue;
                            }
                        }
                    }

                    decimal amount = (latestAct != null && latestAct.ActionPrice > 0) ? latestAct.ActionPrice : itemTotalPrice;
                    string carriedOverMonth = "-";

                    if (latestAct != null)
                    {
                        carriedOverMonth = (latestAct.Action == "Deferred" ? "ค้างรับ (ผ่อนชำระ) มาจาก " : "ค้างรับมาจาก ") + ConvertKeyToThaiMonth(latestAct.MonthYear);
                    }
                    else if (approvedDt.HasValue && $"{approvedDt.Value.Year:0000}-{approvedDt.Value.Month:00}" != currentMonthKey)
                    {
                        carriedOverMonth = "ค้างรับมาจาก " + ConvertKeyToThaiMonth($"{approvedDt.Value.Year:0000}-{approvedDt.Value.Month:00}");
                    }

                    var recordDate = latestAct?.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                        ?? (approvedDt.HasValue ? approvedDt.Value.ToString("dd/MM/yyyy 00:00") : now.ToString("dd/MM/yyyy HH:mm"));

                    formattedActionItems.Add((currentMonthKey, new object?[]
                    {
                        recordDate,
                        approvedDateDisplay,
                        poNo,
                        orderName,
                        qtyDisplay,
                        dept,
                        amount,
                        "ยังไม่รับสินค้า",
                        carriedOverMonth
                    }));
                    processedCurrentMonthOrderIds.Add(otm.Id);
                }
            }

            var actionGroups = formattedActionItems
                .GroupBy(item => item.MonthYear)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    MonthYear = g.Key,
                    Rows = g.OrderBy(x => GetStatusSortOrder(x.Row[7]?.ToString()))
                            .ThenByDescending(x => x.Row[1]?.ToString())
                            .Select(x => x.Row)
                            .ToList()
                }).ToList();

            // 1.2 ประวัติการสั่งซื้อ & ติดตามการรับสินค้า (TiDB)
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

                        // แท็บ ติดตามการรับสินค้า
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

            // 1.3 คลังสินค้า / สต็อกคงเหลือ (StockItems)
            var stockItemRows = new List<object?[]>();
            try
            {
                var allStock = await _context.StockItems.OrderBy(s => s.ProductCode).ToListAsync();
                foreach (var st in allStock)
                {
                    string status = st.StockStatus ?? (st.Quantity <= st.MinStock ? "ใกล้หมด" : "ปกติ");
                    if (st.Quantity <= 0) status = "สินค้าหมด";

                    stockItemRows.Add(new object?[]
                    {
                        st.ProductCode,
                        st.ProductName,
                        st.Category ?? "-",
                        st.InitialStock,
                        st.Quantity,
                        st.MinStock,
                        st.MaxStock,
                        status,
                        st.FilePath ?? "-",
                        st.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch StockItems: {ex.Message}");
            }

            // 1.4 ราคากลางสินค้า (ProductPrices)
            var productPriceRows = new List<object?[]>();
            try
            {
                var allPrices = await _context.ProductPrices.OrderBy(p => p.ProductCode).ToListAsync();
                foreach (var pr in allPrices)
                {
                    productPriceRows.Add(new object?[]
                    {
                        pr.ProductCode,
                        pr.ProductName,
                        pr.Unit ?? "-",
                        pr.TotalQty,
                        pr.TotalValue,
                        pr.PricePerUnit,
                        pr.Sources ?? "-"
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch ProductPrices: {ex.Message}");
            }

            // =========================================================================
            // 📁 ก้อนที่ 2: เตรียมข้อมูลสำหรับ Google Sheet 2 (แผนผลิตและประวัติ)
            // =========================================================================

            // 2.1 แผนผลิตประจำสัปดาห์ (WeeklyPlans + WeeklyPlanDetails)
            var weeklyPlanRows = new List<object?[]>();
            try
            {
                var allPlans = await _context.WeeklyPlans
                    .Include(w => w.Details)
                    .ThenInclude(d => d.MatchedOrder)
                    .OrderByDescending(w => w.UploadedAt)
                    .ToListAsync();

                foreach (var plan in allPlans)
                {
                    string uploadDateDisplay = plan.UploadedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                    string uploader = plan.UploadedBy ?? "-";

                    foreach (var det in plan.Details.OrderBy(d => d.RowIndex))
                    {
                        weeklyPlanRows.Add(new object?[]
                        {
                            plan.FileName,
                            plan.SheetName,
                            uploadDateDisplay,
                            uploader,
                            det.RowIndex,
                            det.PoNumberInFile ?? "-",
                            det.Department ?? "-",
                            det.OrderName ?? "-",
                            det.OrderStatus ?? "-",
                            det.DeliveryTarget ?? "-",
                            det.Price ?? "-",
                            det.IsMatched ? "จับคู่แล้ว" : "ยังไม่จับคู่",
                            det.MatchedOrder?.PoNumber ?? "-"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch WeeklyPlans: {ex.Message}");
            }

            // 2.2 ประวัติการเคลื่อนไหวสต็อก (StockLogs)
            var stockLogRows = new List<object?[]>();
            try
            {
                var allLogs = await _context.StockLogs.OrderByDescending(l => l.Timestamp).ToListAsync();
                foreach (var log in allLogs)
                {
                    stockLogRows.Add(new object?[]
                    {
                        log.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
                        log.StockItemCode,
                        log.Action,
                        log.QuantityChanged,
                        log.ReferenceId ?? "-",
                        log.Remarks ?? "-",
                        log.User ?? "-"
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch StockLogs: {ex.Message}");
            }

            // 2.3 รายงานสรุปภาพรวมคำสั่งซื้อ (Reports)
            var reportRows = new List<object?[]>();
            try
            {
                var allReports = await _context.Reports.OrderByDescending(r => r.CreatedAt).ToListAsync();
                foreach (var rep in allReports)
                {
                    reportRows.Add(new object?[]
                    {
                        rep.ReportName,
                        rep.OriginalFileName,
                        rep.TotalPOs,
                        rep.MatchedPOs,
                        rep.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                        rep.CreatedBy ?? "-"
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch Reports: {ex.Message}");
            }

            // 2.4 หน่วยความจำ AI จับคู่สินค้า (ItemMappings)
            var itemMappingRows = new List<object?[]>();
            try
            {
                var allMappings = await _context.ItemMappings.OrderByDescending(m => m.CreatedAt).ToListAsync();
                foreach (var map in allMappings)
                {
                    itemMappingRows.Add(new object?[]
                    {
                        map.OrderName,
                        map.StockItemCode,
                        map.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sync] Note: Could not fetch ItemMappings: {ex.Message}");
            }

            // =========================================================================
            // 🚀 ยิงส่งออกไปยัง Google Sheets ทั้ง 2 ปลายทาง
            // =========================================================================
            var sheetPayloadPrimary = new
            {
                SheetName_Actions = "บันทึกการรับของประจำเดือน",
                ActionGroups = actionGroups,
                SheetName_OrderHistory = "ประวัติการสั่งซื้อ",
                OrderHistoryRows = orderHistoryRows,
                SheetName_ReceivedTracking = "ติดตามการรับสินค้า",
                ReceivedTrackingGroups = receivedTrackingGroups,
                SheetName_Stock = "คลังสินค้า",
                StockItemRows = stockItemRows,
                SheetName_ProductPrices = "ราคากลางสินค้า",
                ProductPriceRows = productPriceRows
            };

            var sheetPayloadSecondary = new
            {
                SheetName_WeeklyPlans = "แผนผลิตประจำสัปดาห์",
                WeeklyPlanRows = weeklyPlanRows,
                SheetName_StockLogs = "ประวัติการเคลื่อนไหวสต็อก",
                StockLogRows = stockLogRows,
                SheetName_Reports = "รายงานสรุปภาพรวมคำสั่งซื้อ",
                ReportRows = reportRows,
                SheetName_ItemMappings = "หน่วยความจำ AI จับคู่สินค้า",
                ItemMappingRows = itemMappingRows
            };

            var client = _httpClientFactory.CreateClient("GoogleAppsScript");
            client.Timeout = TimeSpan.FromSeconds(180);

            // ส่งชีท 1
            var primaryContent = new StringContent(JsonSerializer.Serialize(sheetPayloadPrimary), System.Text.Encoding.UTF8, "application/json");
            var primaryTask = client.PostAsync(appScriptUrlPrimary, primaryContent);

            // ส่งชีท 2 (ถ้ามีการระบุ URL)
            Task<HttpResponseMessage>? secondaryTask = null;
            if (!string.IsNullOrWhiteSpace(appScriptUrlSecondary) && !appScriptUrlSecondary.Contains("_placeholder"))
            {
                var secondaryContent = new StringContent(JsonSerializer.Serialize(sheetPayloadSecondary), System.Text.Encoding.UTF8, "application/json");
                secondaryTask = client.PostAsync(appScriptUrlSecondary, secondaryContent);
            }

            await Task.WhenAll(secondaryTask != null ? new[] { primaryTask, secondaryTask } : new[] { primaryTask });

            var primaryRes = await primaryTask;
            var secondaryRes = secondaryTask != null ? await secondaryTask : null;

            bool primarySuccess = primaryRes.IsSuccessStatusCode;
            bool secondarySuccess = secondaryRes == null || secondaryRes.IsSuccessStatusCode;

            var details = new
            {
                sheet1 = new
                {
                    success = primarySuccess,
                    statusCode = (int)primaryRes.StatusCode,
                    actions = formattedActionItems.Count,
                    orderHistory = orderHistoryRows.Count,
                    receivedTracking = receivedTrackingItems.Count,
                    stockItems = stockItemRows.Count,
                    productPrices = productPriceRows.Count
                },
                sheet2 = new
                {
                    success = secondarySuccess,
                    statusCode = secondaryRes != null ? (int)secondaryRes.StatusCode : 200,
                    weeklyPlans = weeklyPlanRows.Count,
                    stockLogs = stockLogRows.Count,
                    reports = reportRows.Count,
                    itemMappings = itemMappingRows.Count
                }
            };

            if (primarySuccess && secondarySuccess)
            {
                string msg = $"ส่งข้อมูลลง Google Sheet สำเร็จครบทั้ง 2 ไฟล์!\n" +
                             $"• ชีทหลัก (5 แท็บ): รับของ {formattedActionItems.Count} รายการ, ประวัติสั่งซื้อ {orderHistoryRows.Count} รายการ, ติดตามรับของ {receivedTrackingItems.Count} รายการ, คลังสินค้า {stockItemRows.Count} รายการ, ราคากลาง {productPriceRows.Count} รายการ\n" +
                             $"• ชีทรอง (4 แท็บ): แผนผลิต {weeklyPlanRows.Count} รายการ, ประวัติสต็อก {stockLogRows.Count} รายการ, รายงาน {reportRows.Count} รายการ, AI Mapping {itemMappingRows.Count} รายการ";

                return (true, msg, formattedActionItems.Count, actionGroups.Count, (object)details);
            }
            else
            {
                string err1 = primarySuccess ? "OK" : $"HTTP {(int)primaryRes.StatusCode}";
                string err2 = secondarySuccess ? "OK" : (secondaryRes != null ? $"HTTP {(int)secondaryRes.StatusCode}" : "Not Sent");
                return (false, $"เกิดข้อผิดพลาดในการส่งข้อมูล (ชีท 1: {err1}, ชีท 2: {err2})", formattedActionItems.Count, actionGroups.Count, (object)details);
            }
        }
        catch (Exception ex)
        {
            return (false, $"เกิดข้อผิดพลาดในการเชื่อมต่อ Google Sheets: {ex.Message}", 0, 0, null);
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

    private static int GetStatusSortOrder(string? status)
    {
        return status switch
        {
            "รับสินค้าแล้ว" => 1,
            "ผ่อนชำระ" => 2,
            "ยังไม่รับสินค้า" => 3,
            _ => 4
        };
    }

    private static DateTime? ParseThaiDate(string? thaiDateStr)
    {
        if (string.IsNullOrWhiteSpace(thaiDateStr) || thaiDateStr == "-") return null;

        thaiDateStr = thaiDateStr.Trim();
        if (thaiDateStr.Contains(' '))
        {
            thaiDateStr = thaiDateStr.Split(' ')[0];
        }

        thaiDateStr = thaiDateStr.Replace('-', '/');

        var parts = thaiDateStr.Split('/');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out var p1) &&
            int.TryParse(parts[1], out var p2) &&
            int.TryParse(parts[2], out var p3))
        {
            int day = p1;
            int month = p2;
            int year = p3;

            if (p1 > 1000)
            {
                year = p1;
                month = p2;
                day = p3;
            }
            else if (p2 > 12 && p1 <= 12)
            {
                day = p2;
                month = p1;
                year = p3;
            }

            if (year > 2500) year -= 543;

            try
            {
                return new DateTime(year, month, day);
            }
            catch { }
        }

        return null;
    }
}
