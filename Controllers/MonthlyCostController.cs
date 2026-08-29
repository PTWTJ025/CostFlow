using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using CostFlow.Data;
using CostFlow.Models;
using System.Globalization;
using CostFlow.Services;
using CostFlow.Hubs;

namespace CostFlow.Controllers
{
    [Authorize]
    public class MonthlyCostController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IDateTimeProvider _dateTimeProvider;
        private readonly IHubContext<DashboardHub> _hubContext;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly MonthlyOrderSyncService _syncService;
        private readonly IMockDateStore _mockDateStore;

        public MonthlyCostController(
            AppDbContext context, 
            IDateTimeProvider dateTimeProvider,
            IHubContext<DashboardHub> hubContext, 
            IHttpClientFactory httpClientFactory, 
            IConfiguration configuration,
            MonthlyOrderSyncService syncService,
            IMockDateStore mockDateStore)
        {
            _context = context;
            _dateTimeProvider = dateTimeProvider;
            _hubContext = hubContext;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _syncService = syncService;
            _mockDateStore = mockDateStore;
        }

        // GET: /MonthlyCost
        public async Task<IActionResult> Index(int? year)
        {
            await AutoClosePriorMonthsAsync();
            ViewData["HeaderTitle"] = "คิดค่าใช้จ่ายประจำเดือน";

            // Thai month names (index = month number)
            var thaiMonths = new[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            var now = _dateTimeProvider.Now;
            var selectedYear = year ?? now.Year; // ค่าเริ่มต้น: ปีปัจจุบัน

            // ดึงปีทั้งหมดที่มีข้อมูลจริงในระบบ (จาก MonthlyOrderActions และ OrderTrackingMasters)
            var dbActionYears = await _context.MonthlyOrderActions
                .Where(a => !string.IsNullOrEmpty(a.MonthYear) && a.MonthYear.Length >= 4)
                .Select(a => a.MonthYear.Substring(0, 4))
                .Distinct()
                .ToListAsync();

            var parsedActionYears = dbActionYears
                .Select(y => int.TryParse(y, out var yr) ? yr : (int?)null)
                .Where(y => y.HasValue)
                .Select(y => y!.Value);

            var approvedDateYears = (await _context.OrderTrackingMasters
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .Select(o => o.ApprovedDate)
                    .ToListAsync())
                .Select(d => ParseThaiDate(d))
                .Where(d => d.HasValue)
                .Select(d => d!.Value.Year);

            var availableYears = parsedActionYears
                .Concat(approvedDateYears)
                .Concat(new[] { now.Year, selectedYear })
                .Distinct()
                .OrderByDescending(y => y)
                .ToList();

            ViewBag.AvailableYears = availableYears;
            ViewBag.SelectedYear = selectedYear;

            // Load all actions for the selected year
            // Use MonthYear string prefix ("yyyy-MM") instead of CreatedAt to avoid timezone issues
            var yearPrefix = $"{selectedYear:0000}-";
            var yearEndPrefix = $"{selectedYear + 1:0000}-";

            // โหลด actions ทั้งหมด (รวมปีก่อนหน้า) เพื่อรองรับ cross-year carry-over
            // Bug Fix: เดิมกรองแค่ yearPrefix ทำให้รายการ Skipped/Deferred จาก ธ.ค. ปีก่อนไม่ถูก carry-over มา ม.ค. ปีใหม่
            var allActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .ThenInclude(o => o.Report)
                .Where(moa => moa.MonthYear.StartsWith(yearPrefix) ||
                              string.Compare(moa.MonthYear, yearPrefix) < 0)
                .ToListAsync();

            var allPriorReceivedList = await _context.MonthlyOrderActions
                .Where(moa =>
                    moa.Action == "ReceivedFull" && string.Compare(moa.MonthYear, $"{selectedYear:0000}-12") <= 0)
                .Select(moa => new { moa.MonthYear, moa.OrderTrackingMasterId })
                .ToListAsync();

            var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();

            // ดึงข้อมูล OrderTrackingMasters ทั้งหมดเพื่อคำนวณยอดรวมของแต่ละเดือน (ก่อน action)
            var allOrders = await _context.OrderTrackingMasters
                .Select(o => new { o.Id, o.Amount, o.ApprovedDate })
                .ToListAsync();

            // กำหนดเดือนที่จะแสดงป้าย "มูลค่ารวมสะสม" ให้เป็นเดือนปัจจุบันเสมอสำหรับปีปัจจุบัน
            int latestMonthWithData = selectedYear == now.Year
                ? now.Month
                : (selectedYear < now.Year ? 12 : 1);

            // ประกาศ currentMonthKey ไว้ก่อน loop
            var currentMonthKey = now.ToString("yyyy-MM");

            // Build cards for all 12 months
            var cards = new List<MonthlyCardViewModel>();

            // คำนวณยอดรวมสั่งผลิตสะสมของปีก่อนหน้าทั้งหมด (ก่อนปีที่เลือก) เป็นฐานเริ่มต้น
            decimal runningPlannedAmount = allOrders
                .Where(o =>
                {
                    var d = ParseThaiDate(o.ApprovedDate);
                    return d.HasValue && d.Value.Year < selectedYear;
                })
                .Sum(o => ParseDecimal(o.Amount));

            for (int month = 1; month <= 12; month++)
            {
                var monthKey = $"{selectedYear:0000}-{month:00}";
                var displayName = $"{thaiMonths[month]} {selectedYear + 543}";

                // 1. คำนวณสินค้าที่มีอนุมัติสั่งผลิตในเดือนนี้โดยตรง
                var monthOrdersInMonth = allOrders
                    .Where(o =>
                    {
                        var d = ParseThaiDate(o.ApprovedDate);
                        return d.HasValue && d.Value.Year == selectedYear && d.Value.Month == month;
                    })
                    .ToList();

                var monthBasePlannedAmount = monthOrdersInMonth
                    .Sum(o => ParseDecimal(o.Amount));

                runningPlannedAmount += monthBasePlannedAmount;

                var monthActions = allActions.Where(a => a.MonthYear == monthKey).ToList();
                var receivedActions = monthActions.Where(a => a.Action == "ReceivedFull").ToList();
                var skippedActions = monthActions.Where(a => a.Action == "Skipped").ToList();

                int receivedCount = receivedActions.Count;
                int doneItems = receivedCount;
                decimal receivedAmount = receivedActions.Sum(a => a.ActionPrice);

                // 2. คำนวณ Carry-over จากเดือนก่อนหน้า (ถ้างวดก่อนหน้าปิดแล้ว)
                var priorMonthActionMap = allActions
                    .Where(a => string.Compare(a.MonthYear, monthKey) < 0)
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key,
                        g => g.OrderByDescending(x => x.MonthYear).ThenByDescending(x => x.CreatedAt).First());

                var existingActionOrderIds = monthActions.Select(ma => ma.OrderTrackingMasterId).ToHashSet();

                // เช็คว่าเป็นเดือนปัจจุบันหรืออนาคต
                var isPastMonth = string.Compare(monthKey, currentMonthKey) < 0;
                var isFutureMonth = string.Compare(monthKey, currentMonthKey) > 0;

                var pendingCarryOverItems = priorMonthActionMap
                    .Where(kvp => !existingActionOrderIds.Contains(kvp.Key))
                    .Where(kvp =>
                    {
                        var pp = kvp.Value.MonthYear.Split('-');
                        var tp = monthKey.Split('-');
                        var isExactPrevMonth = false;
                        if (pp.Length == 2 && tp.Length == 2
                                           && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                           && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                        {
                            var monthDiff = ((tY - pY) * 12) + (tM - pM);
                            isExactPrevMonth = monthDiff == 1;
                        }

                        if (kvp.Value.Action == "Deferred" && isExactPrevMonth)
                        {
                            return true;
                        }
                        else if (kvp.Value.Action == "Skipped")
                        {
                            if (pp.Length == 2 && tp.Length == 2
                                               && int.TryParse(pp[0], out pY) && int.TryParse(pp[1], out pM)
                                               && int.TryParse(tp[0], out tY) && int.TryParse(tp[1], out tM))
                            {
                                var monthDiff = ((tY - pY) * 12) + (tM - pM);
                                return monthDiff >= 1;
                            }
                        }

                        return false;
                    })
                    .ToList();

                // นับ Deferred/Skipped carry-over (ได้กรองเดือนอนาคตออกไปแล้ว)
                int deferredCount = 0;
                int skippedCount = skippedActions.Count;
                decimal deferredAmount = 0m;
                decimal skippedAmount = skippedActions.Sum(a => ParseDecimal(a.OrderTrackingMaster?.Amount));
                decimal carryOverPlannedAmount = 0m;

                foreach (var kvp in pendingCarryOverItems)
                {
                    var otm = allActions.FirstOrDefault(a => a.OrderTrackingMasterId == kvp.Key)?.OrderTrackingMaster;
                    var amt = otm != null ? ParseDecimal(otm.Amount) : 0m;

                    var isExactPrevMonth = false;
                    var pp = kvp.Value.MonthYear.Split('-');
                    var tp = monthKey.Split('-');
                    if (pp.Length == 2 && tp.Length == 2
                                       && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                       && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                    {
                        var monthDiff = ((tY - pY) * 12) + (tM - pM);
                        isExactPrevMonth = monthDiff == 1;
                    }

                    if (kvp.Value.Action == "Deferred" && isExactPrevMonth)
                    {
                        if (isPastMonth)
                        {
                            // ถ้าเดือนผ่านไปแล้วแต่ไม่ action -> ถือว่า auto-skipped
                            skippedCount++;
                            skippedAmount += (kvp.Value.ActionPrice > 0 ? kvp.Value.ActionPrice : amt);
                            carryOverPlannedAmount += (kvp.Value.ActionPrice > 0 ? kvp.Value.ActionPrice : amt);
                        }
                        else
                        {
                            deferredCount++;
                            deferredAmount += kvp.Value.ActionPrice;
                            carryOverPlannedAmount += (kvp.Value.ActionPrice > 0 ? kvp.Value.ActionPrice : amt);
                        }
                    }
                    else
                    {
                        // ถ้าเป็น Skipped หรือเป็น Deferred ที่เก่ากว่า 1 เดือน (ถูก auto-skipped)
                        skippedCount++;
                        skippedAmount += amt;
                        carryOverPlannedAmount += amt;
                    }
                }

                if (isPastMonth)
                {
                    // Items originating in this month that were never actioned are automatically skipped
                    var autoSkippedOrders = monthOrdersInMonth
                        .Where(o => !existingActionOrderIds.Contains(o.Id))
                        .ToList();

                    skippedCount += autoSkippedOrders.Count;
                    skippedAmount += autoSkippedOrders.Sum(o => ParseDecimal(o.Amount));
                }

                int receivedCarryOver = receivedActions.Count(a =>
                    priorMonthActionMap.TryGetValue(a.OrderTrackingMasterId, out var prior) &&
                    (prior.Action == "Deferred" || prior.Action == "Skipped"));

                int deferredCarryOver = deferredCount;
                int skippedCarryOver = pendingCarryOverItems.Count(kvp => kvp.Value.Action == "Skipped");

                // 3. เช็คว่าเดือนนี้มีข้อมูลสั่งผลิตใหม่ หรือมีรายการค้างยกมาจากเดือนก่อนหรือไม่
                int hasNewOrCarryOverOrders = monthOrdersInMonth.Count + pendingCarryOverItems.Count;
                int totalItemsInMonth = monthOrdersInMonth.Count + pendingCarryOverItems.Count;
                int pendingItems = hasNewOrCarryOverOrders == 0
                    ? 0
                    : (isPastMonth ? 0 : Math.Max(0, totalItemsInMonth - monthActions.Count));
                int totalItems = hasNewOrCarryOverOrders == 0 ? 0 : totalItemsInMonth;

                decimal plannedTotalForMonth = monthBasePlannedAmount + carryOverPlannedAmount;

                // Determine status
                string statusCode, statusLabel;

                var isCurrentMonth = monthKey == currentMonthKey;
                var isMonthInFuture = string.Compare(monthKey, currentMonthKey) > 0;

                if (hasNewOrCarryOverOrders == 0)
                {
                    statusCode = "empty";
                    statusLabel = "ไม่มีข้อมูล";
                }
                else if (isMonthInFuture)
                {
                    statusCode = "future";
                    statusLabel = "รอดำเนินการ";
                }
                else if (pendingItems == 0 && totalItems > 0)
                {
                    statusCode = "completed";
                    statusLabel = "เสร็จสมบูรณ์";
                }
                else if (isCurrentMonth)
                {
                    statusCode = "active";
                    statusLabel = "กำลังดำเนินการ";
                }
                else
                {
                    statusCode = "pending";
                    statusLabel = "ค้างดำเนินการ";
                }

                var reportName = string.Empty;
                if (monthActions.Any())
                {
                    var latestAction = monthActions.OrderByDescending(a => a.CreatedAt).First();
                    reportName = latestAction.OrderTrackingMaster?.Report?.ReportName ?? string.Empty;
                }

                cards.Add(new MonthlyCardViewModel
                {
                    MonthYearDisplay = displayName,
                    MonthYearKey = monthKey,
                    FileName = reportName,
                    StatusCode = statusCode,
                    StatusLabel = statusLabel,
                    TotalItems = totalItems,
                    DoneItems = doneItems,
                    PendingItems = pendingItems,
                    ReceivedCount = receivedCount,
                    ReceivedAmount = receivedAmount,
                    DeferredCount = deferredCount,
                    DeferredAmount = deferredAmount,
                    SkippedCount = skippedCount,
                    SkippedAmount = skippedAmount,
                    ReceivedCarryOver = receivedCarryOver,
                    DeferredCarryOver = deferredCarryOver,
                    SkippedCarryOver = skippedCarryOver,
                    MonthAmount = monthBasePlannedAmount,
                    TotalAmount = (month == latestMonthWithData && runningPlannedAmount > 0)
                        ? runningPlannedAmount
                        : (monthOrdersInMonth.Count == 0 && monthActions.Count == 0 ? 0m : runningPlannedAmount),
                    IsLatestWithData = (month == latestMonthWithData),
                    IsPastYearDecember = (selectedYear < now.Year && month == 12)
                });
            }

            var viewModel = new MonthlyCostIndexViewModel { Cards = cards };
            return View(viewModel);
        }

        // GET: /MonthlyCost/GetCardsData?year=xxx — API สำหรับ polling realtime
        [HttpGet]
        public async Task<IActionResult> GetCardsData(int? year)
        {
            var now = _dateTimeProvider.Now;
            var selectedYear = year ?? now.Year;
            var currentMonthKey = now.ToString("yyyy-MM");

            var thaiMonths = new[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            var yearPrefix = $"{selectedYear:0000}-";

            var allActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .Where(moa => moa.MonthYear.StartsWith(yearPrefix) ||
                              string.Compare(moa.MonthYear, yearPrefix) < 0)
                .ToListAsync();

            var allOrders = await _context.OrderTrackingMasters
                .Select(o => new { o.Id, o.Amount, o.ApprovedDate })
                .ToListAsync();

            int latestMonthWithData = selectedYear == now.Year
                ? now.Month
                : (selectedYear < now.Year ? 12 : 1);

            var result = new List<object>();
            // คำนวณยอดรวมสั่งผลิตสะสมของปีก่อนหน้าทั้งหมด (ก่อนปีที่เลือก) เป็นฐานเริ่มต้น
            decimal runningPlannedAmount = allOrders
                .Where(o =>
                {
                    var d = ParseThaiDate(o.ApprovedDate);
                    return d.HasValue && d.Value.Year < selectedYear;
                })
                .Sum(o => ParseDecimal(o.Amount));

            for (int month = 1; month <= 12; month++)
            {
                var monthKey = $"{selectedYear:0000}-{month:00}";
                var monthActions = allActions.Where(a => a.MonthYear == monthKey).ToList();

                var monthOrdersInMonth = allOrders
                    .Where(o =>
                    {
                        var d = ParseThaiDate(o.ApprovedDate);
                        return d.HasValue && d.Value.Year == selectedYear && d.Value.Month == month;
                    })
                    .ToList();

                var monthBasePlannedAmount = monthOrdersInMonth
                    .Sum(o => ParseDecimal(o.Amount));

                runningPlannedAmount += monthBasePlannedAmount;

                var receivedActions = monthActions.Where(a => a.Action == "ReceivedFull").ToList();
                var skippedActions = monthActions.Where(a => a.Action == "Skipped").ToList();

                var receivedAmount = receivedActions.Sum(a => a.ActionPrice);

                var priorMonthActionMap = allActions
                    .Where(a => string.Compare(a.MonthYear, monthKey) < 0)
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key,
                        g => g.OrderByDescending(x => x.MonthYear).ThenByDescending(x => x.CreatedAt).First());

                var existingIds = monthActions.Select(a => a.OrderTrackingMasterId).ToHashSet();

                var pendingCarryOver = priorMonthActionMap
                    .Where(kvp => !existingIds.Contains(kvp.Key))
                    .Where(kvp =>
                    {
                        var pp = kvp.Value.MonthYear.Split('-');
                        var tp = monthKey.Split('-');
                        var isExactPrevMonth = false;
                        if (pp.Length == 2 && tp.Length == 2
                                           && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                           && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                        {
                            var monthDiff = ((tY - pY) * 12) + (tM - pM);
                            isExactPrevMonth = monthDiff == 1;

                            if (kvp.Value.Action == "Deferred" && isExactPrevMonth)
                            {
                                return true;
                            }
                            else if (kvp.Value.Action == "Skipped")
                            {
                                return monthDiff >= 1;
                            }
                        }

                        return false;
                    }).ToList();

                var isPastMonth = string.Compare(monthKey, currentMonthKey) < 0;

                int deferredCount = 0;
                int skippedCount = skippedActions.Count;
                decimal deferredAmount = 0m;
                decimal skippedAmount = skippedActions.Sum(a => ParseDecimal(a.OrderTrackingMaster?.Amount));
                decimal carryOverPlannedAmount = 0m;

                foreach (var kvp in pendingCarryOver)
                {
                    var otm = allActions.FirstOrDefault(a => a.OrderTrackingMasterId == kvp.Key)?.OrderTrackingMaster;
                    var amt = otm != null ? ParseDecimal(otm.Amount) : 0m;

                    var isExactPrevMonth = false;
                    var pp = kvp.Value.MonthYear.Split('-');
                    var tp = monthKey.Split('-');
                    if (pp.Length == 2 && tp.Length == 2
                                       && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                       && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                    {
                        var monthDiff = ((tY - pY) * 12) + (tM - pM);
                        isExactPrevMonth = monthDiff == 1;
                    }

                    if (kvp.Value.Action == "Deferred" && isExactPrevMonth)
                    {
                        if (isPastMonth)
                        {
                            skippedCount++;
                            skippedAmount += (kvp.Value.ActionPrice > 0 ? kvp.Value.ActionPrice : amt);
                            carryOverPlannedAmount += (kvp.Value.ActionPrice > 0 ? kvp.Value.ActionPrice : amt);
                        }
                        else
                        {
                            deferredCount++;
                            deferredAmount += kvp.Value.ActionPrice;
                            carryOverPlannedAmount += (kvp.Value.ActionPrice > 0 ? kvp.Value.ActionPrice : amt);
                        }
                    }
                    else
                    {
                        skippedCount++;
                        skippedAmount += amt;
                        carryOverPlannedAmount += amt;
                    }
                }

                if (isPastMonth)
                {
                    // Items originating in this month that were never actioned are automatically skipped
                    var autoSkippedOrders = monthOrdersInMonth
                        .Where(o => !existingIds.Contains(o.Id))
                        .ToList();

                    skippedCount += autoSkippedOrders.Count;
                    skippedAmount += autoSkippedOrders.Sum(o => ParseDecimal(o.Amount));
                }

                var hasNewOrCarryOverOrders = monthOrdersInMonth.Count + pendingCarryOver.Count;
                var plannedTotalForMonth = monthBasePlannedAmount + carryOverPlannedAmount;
                result.Add(new
                {
                    monthKey,
                    receivedCount = receivedActions.Count,
                    receivedAmount = receivedAmount,
                    deferredCount,
                    deferredAmount,
                    skippedCount,
                    skippedAmount,
                    monthAmount = monthBasePlannedAmount,
                    totalAmount = (month == latestMonthWithData && runningPlannedAmount > 0)
                        ? runningPlannedAmount
                        : (monthOrdersInMonth.Count == 0 && monthActions.Count == 0 ? 0m : runningPlannedAmount),
                    paidAmount = receivedAmount,
                    isPastYearDecember = (selectedYear < now.Year && month == 12)
                });
            }

            selectedYear = year ?? now.Year;

            var dbActionYears = await _context.MonthlyOrderActions
                .Where(a => !string.IsNullOrEmpty(a.MonthYear) && a.MonthYear.Length >= 4)
                .Select(a => a.MonthYear.Substring(0, 4))
                .Distinct()
                .ToListAsync();

            var parsedActionYears = dbActionYears
                .Select(y => int.TryParse(y, out var yr) ? yr : (int?)null)
                .Where(y => y.HasValue)
                .Select(y => y!.Value);

            var approvedDateYears = (await _context.OrderTrackingMasters
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .Select(o => o.ApprovedDate)
                    .ToListAsync())
                .Select(d => ParseThaiDate(d))
                .Where(d => d.HasValue)
                .Select(d => d!.Value.Year);

            var availableYears = parsedActionYears
                .Concat(approvedDateYears)
                .Concat(new[] { now.Year, selectedYear })
                .Distinct()
                .OrderByDescending(y => y)
                .ToList();

            return Json(new { cards = result, availableYears });
        }

        // GET: /MonthlyCost/Detail/{id}
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Detail(string id)
        {
            await AutoClosePriorMonthsAsync();

            if (string.IsNullOrEmpty(id))
            {
                id = "กรกฎาคม 2569";
            }

            // Parse month/year from Thai format (e.g., "กรกฎาคม 2569" หรือ "2026-02")
            var monthYearKey = ConvertThaiMonthToKey(id);
            var canonicalThaiMonth = ConvertKeyToThaiMonth(monthYearKey);

            // ถ้า URL ที่เข้ามาไม่ใช่ชื่อเดือนภาษาไทยมาตรฐาน (เช่น เข้ามาเป็น 2026-02 หรือ 2026-22) ให้ Redirect ไป URL ชื่อเดือนภาษาไทยทันที
            if (!string.Equals(id, canonicalThaiMonth, StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Detail", new { id = canonicalThaiMonth });
            }

            // ประกาศ currentMonthKey สำหรับเช็คว่าเป็นเดือนอนาคตหรือไม่
            var now = _dateTimeProvider.Now;
            var currentMonthKey = now.ToString("yyyy-MM");

            // Get existing actions for this month along with related order details
            var existingActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .ThenInclude(otm => otm.MatchedInWeeklyPlans)
                .ThenInclude(wpd => wpd.WeeklyPlan)
                .Where(moa => moa.MonthYear == monthYearKey)
                .OrderByDescending(moa => moa.CreatedAt)
                .ToListAsync();

            var existingActionIds = existingActions.Select(ea => ea.OrderTrackingMasterId).ToHashSet();

            // Get all prior actions strictly earlier than this monthYearKey
            var priorActions = await _context.MonthlyOrderActions
                .Where(moa => string.Compare(moa.MonthYear, monthYearKey) < 0)
                .OrderByDescending(moa => moa.MonthYear)
                .ThenByDescending(moa => moa.CreatedAt)
                .ToListAsync();

            var latestPriorActionMap = priorActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .ToDictionary(g => g.Key, g => g.First());

            // IDs of orders that were Deferred or Skipped in a prior month → must carry-over to this month
            // For Deferred: only carry over if the prior action is from the IMMEDIATELY preceding month (exactly 1 month)
            //               AND the target month is NOT a future month
            // For Skipped: carry over from any prior month (can skip multiple months)
            //              BUT NOT if the target month is a future month
            var carryOverIds = latestPriorActionMap
                .Where(kvp =>
                {
                    if (kvp.Value.Action == "Deferred")
                    {
                        // Deferred: carry-over เฉพาะเดือนถัดไปทันที (ตกลงจ่ายแล้ว ต้องรับเดือนหน้า)
                        var pp = kvp.Value.MonthYear.Split('-');
                        var tp = monthYearKey.Split('-');
                        if (pp.Length == 2 && tp.Length == 2
                                           && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                           && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                        {
                            var monthDiff = ((tY - pY) * 12) + (tM - pM);
                            return monthDiff == 1;
                        }
                    }
                    else if (kvp.Value.Action == "Skipped")
                    {
                        // Skipped: ของยังไม่มา ลอยข้ามเดือนได้เรื่อยๆ จนกว่าจะตัดสินใจ
                        var pp = kvp.Value.MonthYear.Split('-');
                        var tp = monthYearKey.Split('-');
                        if (pp.Length == 2 && tp.Length == 2
                                           && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                           && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                        {
                            var monthDiff = ((tY - pY) * 12) + (tM - pM);
                            return monthDiff >= 1;
                        }
                    }

                    return false;
                })
                .Select(kvp => kvp.Key)
                .ToHashSet();

            // Parse this month's date range (Gregorian)
            var keyParts = monthYearKey.Split('-');
            DateTime monthStart = DateTime.MinValue, monthEnd = DateTime.MaxValue;
            if (keyParts.Length == 2 &&
                int.TryParse(keyParts[0], out var kYear) &&
                int.TryParse(keyParts[1], out var kMonth))
            {
                monthStart = new DateTime(kYear, kMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                monthEnd = monthStart.AddMonths(1);
            }

            // Fetch orders that either:
            //   (a) belong to THIS month (ApprovedDate in this month), OR
            //   (b) are carry-overs (Deferred / Skipped from a prior month)
            // — and have not yet been actioned in this month
            var allPendingOrders = await _context.OrderTrackingMasters
                .Include(otm => otm.Report)
                .Include(otm => otm.MatchedInWeeklyPlans)
                .ThenInclude(wpd => wpd.WeeklyPlan)
                .Where(otm => !existingActionIds.Contains(otm.Id))
                .OrderBy(otm => otm.PoNumber)
                .ToListAsync();

            // Filter: แสดงทุก WO ที่ ApprovedDate ≤ เดือนปัจจุบัน และยังไม่รับครบ
            // (ไม่ว่าจะเคยถูก Skipped/Deferred หรือไม่ก็ตาม ลอยมาเรื่อยๆ จนกว่าจะรับครบ)
            allPendingOrders = allPendingOrders
                .Where(otm =>
                {
                    // ถ้าเคยรับครบในเดือนก่อน → ไม่ต้องแสดงอีก
                    if (latestPriorActionMap.TryGetValue(otm.Id, out var prevCheck) &&
                        prevCheck.Action == "ReceivedFull")
                        return false;

                    var approvedDate = ParseThaiDate(otm.ApprovedDate);
                    if (approvedDate == null)
                        return false;

                    // แสดงทุก WO ที่ ApprovedDate อยู่ในเดือนนี้หรือก่อนหน้า
                    return approvedDate.Value < monthEnd;
                })
                .ToList();


            // Transform existingActions to SavedOrderItem
            var savedItems = existingActions
                .Select(moa =>
                {
                    var otm = moa.OrderTrackingMaster;
                    var quantity = 1;
                    if (!string.IsNullOrEmpty(otm?.RemarksQuantity) && int.TryParse(otm.RemarksQuantity, out var qty))
                    {
                        quantity = qty;
                    }

                    var amount = 0m;
                    if (!string.IsNullOrEmpty(otm?.Amount) && decimal.TryParse(otm.Amount, out var amt))
                    {
                        amount = amt;
                    }

                    var productName = otm?.Remarks ?? "ไม่ระบุ";

                    var latestMatchedPlan = otm?.MatchedInWeeklyPlans
                        .OrderByDescending(w => w.WeeklyPlan.UploadedAt)
                        .FirstOrDefault();

                    var targetDelivery = !string.IsNullOrWhiteSpace(latestMatchedPlan?.DeliveryTarget)
                        ? latestMatchedPlan.DeliveryTarget
                        : "-";
                    var department = !string.IsNullOrEmpty(latestMatchedPlan?.Department)
                        ? latestMatchedPlan.Department
                        : (!string.IsNullOrEmpty(otm?.Urgency) ? otm.Urgency : "-");

                    var parsedApprDate = ParseThaiDate(otm?.ApprovedDate);
                    var originalMonth = parsedApprDate.HasValue
                        ? ConvertKeyToThaiMonth($"{parsedApprDate.Value.Year:0000}-{parsedApprDate.Value.Month:00}")
                        : string.Empty;

                    var forwardedStatus = string.Empty;
                    var forwardedFromMonth = string.Empty;

                    if (otm != null && latestPriorActionMap.TryGetValue(otm.Id, out var priorAction))
                    {
                        var isExactPrevMonth = false;
                        var pp = priorAction.MonthYear.Split('-');
                        var tp = monthYearKey.Split('-');
                        if (pp.Length == 2 && tp.Length == 2
                                           && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                           && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                        {
                            var monthDiff = ((tY - pY) * 12) + (tM - pM);
                            isExactPrevMonth = monthDiff == 1;
                        }

                        if (priorAction.Action == "Deferred" && isExactPrevMonth)
                        {
                            forwardedStatus = "Deferred";
                            forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                        }
                        else
                        {
                            forwardedStatus = "Skipped";
                            if (priorAction.Action == "Skipped")
                            {
                                forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                            }
                            else
                            {
                                var prevMonth = monthStart.AddMonths(-1);
                                forwardedFromMonth =
                                    ConvertKeyToThaiMonth($"{prevMonth.Year:0000}-{prevMonth.Month:00}");
                            }
                        }
                    }
                    else if (parsedApprDate.HasValue && parsedApprDate.Value < monthStart)
                    {
                        forwardedStatus = "Skipped";
                        var prevMonth = monthStart.AddMonths(-1);
                        forwardedFromMonth = ConvertKeyToThaiMonth($"{prevMonth.Year:0000}-{prevMonth.Month:00}");
                    }

                    return new SavedOrderItem
                    {
                        ActionId = moa.Id,
                        OrderId = moa.OrderTrackingMasterId,
                        PoNumber = otm?.PoNumber ?? "N/A",
                        OrderName = productName,
                        Quantity = quantity,
                        TotalPrice = amount,
                        ActionPrice = moa.ActionPrice,
                        Action = moa.Action,
                        DeliveryTarget = targetDelivery,
                        Department = department,
                        CreatedAt = moa.CreatedAt,
                        ForwardedStatus = forwardedStatus,
                        ForwardedFromMonth = forwardedFromMonth,
                        OriginalMonth = originalMonth
                    };
                })
                .ToList();

            var isPastMonth = string.Compare(monthYearKey, currentMonthKey) < 0;

            // Transform to PendingOrderItem with cross-month forwarding logic
            var pendingItems = new List<PendingOrderItem>();
            foreach (var otm in allPendingOrders)
            {
                if (latestPriorActionMap.TryGetValue(otm.Id, out var priorAction))
                {
                    // หากเคยรับสินค้าชำระเต็มไปแล้วในเดือนก่อน ถือว่าเสร็จสิ้น ไม่ต้องขึ้นอีก
                    if (priorAction.Action == "ReceivedFull")
                    {
                        continue;
                    }
                }

                var quantity = 1;
                if (!string.IsNullOrEmpty(otm.RemarksQuantity) && int.TryParse(otm.RemarksQuantity, out var qty))
                {
                    quantity = qty;
                }

                var amount = 0m;
                if (!string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var amt))
                {
                    amount = amt;
                }

                var forwardedStatus = string.Empty;
                var forwardedFromMonth = string.Empty;

                if (priorAction != null)
                {
                    var isExactPrevMonth = false;
                    var pp = priorAction.MonthYear.Split('-');
                    var tp = monthYearKey.Split('-');
                    if (pp.Length == 2 && tp.Length == 2
                                       && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                       && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                    {
                        var monthDiff = ((tY - pY) * 12) + (tM - pM);
                        isExactPrevMonth = monthDiff == 1;
                    }

                    if (priorAction.Action == "Deferred" && isExactPrevMonth)
                    {
                        forwardedStatus = "Deferred";
                        // แสดงเดือนที่กดผ่อนล่าสุด (priorAction.MonthYear)
                        forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                        if (priorAction.ActionPrice > 0)
                        {
                            amount = priorAction.ActionPrice;
                        }
                    }
                    else
                    {
                        forwardedStatus = "Skipped";
                        if (priorAction.Action == "Skipped")
                        {
                            // แสดงเดือนที่กดค้างล่าสุด (priorAction.MonthYear)
                            forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                        }
                        else
                        {
                            // เคยผ่อนไว้ แต่ไม่ได้ทำรายการต่อในเดือนถัดมา ถือว่าถูกค้างข้ามเดือน (implicitly skipped)
                            var prevMonth = monthStart.AddMonths(-1);
                            forwardedFromMonth = ConvertKeyToThaiMonth($"{prevMonth.Year:0000}-{prevMonth.Month:00}");
                        }

                        var earlierDeferred = priorActions
                            .Where(a => a.OrderTrackingMasterId == otm.Id && a.Action == "Deferred")
                            .OrderByDescending(a => a.MonthYear)
                            .ThenByDescending(a => a.CreatedAt)
                            .FirstOrDefault();
                        if (earlierDeferred != null && earlierDeferred.ActionPrice > 0)
                        {
                            amount = earlierDeferred.ActionPrice;
                        }
                    }
                }
                else
                {
                    var parsedApprDate = ParseThaiDate(otm.ApprovedDate);
                    if (parsedApprDate.HasValue && parsedApprDate.Value < monthStart)
                    {
                        forwardedStatus = "Skipped";
                        // แสดงเดือนก่อนหน้า (เดือนล่าสุดที่ควรตัดสินใจแต่ไม่ได้กด action)
                        var prevMonth = monthStart.AddMonths(-1);
                        forwardedFromMonth = ConvertKeyToThaiMonth(
                            $"{prevMonth.Year:0000}-{prevMonth.Month:00}");
                    }
                }

                var productName = otm.Remarks ?? "ไม่ระบุ";

                var latestMatchedPlan = otm.MatchedInWeeklyPlans
                    .OrderByDescending(w => w.WeeklyPlan.UploadedAt)
                    .FirstOrDefault();

                var targetDelivery = !string.IsNullOrWhiteSpace(latestMatchedPlan?.DeliveryTarget)
                    ? latestMatchedPlan.DeliveryTarget
                    : "-";
                var department = !string.IsNullOrEmpty(latestMatchedPlan?.Department)
                    ? latestMatchedPlan.Department
                    : (!string.IsNullOrEmpty(otm.Urgency) ? otm.Urgency : "-");

                var originalMonth = string.Empty;
                var approvedDate = ParseThaiDate(otm.ApprovedDate);
                if (approvedDate.HasValue)
                {
                    originalMonth =
                        ConvertKeyToThaiMonth($"{approvedDate.Value.Year:0000}-{approvedDate.Value.Month:00}");
                }
                else if (otm.Report?.CreatedAt != null)
                {
                    // Fallback: ถ้าไม่มี ApprovedDate ให้ใช้ Report.CreatedAt
                    var reportDate = otm.Report.CreatedAt;
                    originalMonth = ConvertKeyToThaiMonth($"{reportDate.Year:0000}-{reportDate.Month:00}");
                }

                if (isPastMonth)
                {
                    // สำหรับเดือนที่ผ่านไปแล้ว (เช่น กรกฎาคม 2569): รายการค้างที่ยังไม่ถูกกด action
                    // ถือเป็น "ผลัดอัตโนมัติไปเดือนถัดไป" (Auto-Skipped) → ย้ายไปอยู่ใน savedItems (แท็บบันทึกแล้ว)
                    savedItems.Add(new SavedOrderItem
                    {
                        ActionId = Guid.Empty,
                        OrderId = otm.Id,
                        PoNumber = otm.PoNumber ?? "N/A",
                        OrderName = productName,
                        Quantity = quantity,
                        TotalPrice = amount,
                        ActionPrice = amount,
                        Action = "Skipped",
                        DeliveryTarget = targetDelivery,
                        Department = department,
                        CreatedAt = monthEnd.AddSeconds(-1)
                    });
                }
                else
                {
                    pendingItems.Add(new PendingOrderItem
                    {
                        OrderId = otm.Id,
                        PoNumber = otm.PoNumber ?? "N/A",
                        OrderName = productName,
                        Quantity = quantity,
                        UnitPrice = quantity > 0 ? amount / quantity : amount,
                        TotalPrice = amount,
                        DeliveryTarget = targetDelivery,
                        Department = department,
                        FileName = otm.Report?.ReportName ?? "N/A",
                        Status = otm.Status ?? "Pending",
                        ForwardedStatus = forwardedStatus,
                        ForwardedFromMonth = forwardedFromMonth,
                        OriginalReportName = otm.Report?.ReportName ?? "N/A",
                        OriginalMonth = originalMonth
                    });
                }
            }

            // Calculate stats
            var totalPlannedAmount = existingActions
                .Sum(ea =>
                {
                    if (ea.Action == "ReceivedFull" && ea.ActionPrice > 0)
                        return ea.ActionPrice;
                    if (ea.Action == "Deferred" && ea.ActionPrice > 0)
                        return ea.ActionPrice;
                    if (ea.OrderTrackingMaster != null &&
                        !string.IsNullOrEmpty(ea.OrderTrackingMaster.Amount) &&
                        decimal.TryParse(ea.OrderTrackingMaster.Amount, out var realAmt))
                        return realAmt;
                    return 0m;
                });

            totalPlannedAmount += pendingItems.Sum(p => p.TotalPrice);
            totalPlannedAmount += savedItems.Where(s => s.ActionId == Guid.Empty).Sum(s => s.TotalPrice);

            var processedAmount = existingActions
                .Where(ea => ea.Action == "ReceivedFull")
                .Sum(ea => ea.ActionPrice);

            var stats = new MonthlyStats
            {
                TotalOrders = pendingItems.Count + savedItems.Count,
                TotalPlannedAmount = totalPlannedAmount,
                ProcessedOrders = existingActions.Count(ea => ea.Action == "ReceivedFull"),
                ProcessedAmount = processedAmount
            };

            string displayMonthThai = ConvertKeyToThaiMonth(monthYearKey);

            var viewModel = new MonthlyCostDetailViewModel
            {
                MonthYear = displayMonthThai,
                PendingOrders = pendingItems,
                SavedOrders = savedItems,
                Stats = stats,
                IsCurrentMonth = (monthYearKey == currentMonthKey),
                IsFutureMonth = string.Compare(monthYearKey, currentMonthKey) > 0
            };

            ViewData["HeaderTitle"] = $"จัดการค่าใช้จ่าย - {displayMonthThai}";
            return View(viewModel);
        }

        private string ConvertThaiMonthToKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return _dateTimeProvider.Now.ToString("yyyy-MM");
            input = input.Trim();

            // 1. ตรวจสอบกรณีเป็นรูปแบบ yyyy-MM ตรงๆ (เช่น "2026-01", "2026-02")
            if (input.Contains("-"))
            {
                var p = input.Split('-');
                if (p.Length == 2 && int.TryParse(p[0], out var y) && int.TryParse(p[1], out var m))
                {
                    if (m >= 1 && m <= 12)
                    {
                        var gregYear = y > 2400 ? y - 543 : y;
                        return $"{gregYear:0000}-{m:02}";
                    }
                }
            }

            // 2. ตรวจสอบกรณีเป็นชื่อเดือนภาษาไทย (เช่น "กุมภาพันธ์ 2569")
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var thaiMonths = new Dictionary<string, int>
                {
                    { "มกราคม", 1 }, { "กุมภาพันธ์", 2 }, { "มีนาคม", 3 }, { "เมษายน", 4 },
                    { "พฤษภาคม", 5 }, { "มิถุนายน", 6 }, { "กรกฎาคม", 7 }, { "สิงหาคม", 8 },
                    { "กันยายน", 9 }, { "ตุลาคม", 10 }, { "พฤศจิกายน", 11 }, { "ธันวาคม", 12 }
                };

                if (int.TryParse(parts[1], out var buddhistYear) && thaiMonths.TryGetValue(parts[0], out var month))
                {
                    var year = buddhistYear > 2400 ? buddhistYear - 543 : buddhistYear; // Convert Buddhist year to Gregorian
                    return $"{year:0000}-{month:00}";
                }
            }

            return _dateTimeProvider.Now.ToString("yyyy-MM");
        }

        private string ConvertKeyToThaiMonth(string key, bool shortYear = false)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.Contains("-")) return key ?? string.Empty;

            key = key.Trim();
            var parts = key.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var year)) return key;

            var monthStr = parts[1].Trim();
            if (monthStr.Length > 2)
            {
                monthStr = monthStr.Substring(0, 2);
            }
            if (!int.TryParse(monthStr, out var month)) return key;

            var thaiMonths = new string[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            if (month < 1 || month > 12) return key;
            
            int thaiYear = year + 543;
            if (shortYear)
            {
                thaiYear = thaiYear % 100;
            }
            
            return $"{thaiMonths[month]} {thaiYear}";
        }

        private string FormatQuantityDisplay(string? remarksQty, string? remarks)
        {
            string val = "";
            if (!string.IsNullOrWhiteSpace(remarksQty) && remarksQty != "-")
            {
                val = remarksQty.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(remarks))
            {
                val = ExtractQuantityFromText(remarks);
            }

            if (string.IsNullOrWhiteSpace(val) || val == "-") return "-";
            if (val.EndsWith("ชิ้น")) return val;
            return $"{val} ชิ้น";
        }

        private static string ExtractQuantityFromText(string remarks)
        {
            if (string.IsNullOrEmpty(remarks)) return "-";
            string clean = remarks.Replace("\u200b", "").Trim();
            string[] patterns =
            {
                @"(?:จำนวน|จํานวน|จำนวนชิ้น|จํานวนชิ้น|จำนวน\s*ชิ้น|จำนวณ|จนวน|จํนวน|จำนวน)\s*[:=\-\s]*\s*([0-9]+)",
                @"([0-9]+)\s*(?:ชิ้น|อัน|ตัว|เครื่อง)"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(clean, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value;
                }
            }

            return "-";
        }

        private static decimal ParseDecimal(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return 0m;
            val = val.Replace(",", "").Replace("฿", "").Trim();
            return decimal.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var res)
                ? res
                : 0m;
        }

        [HttpPost]
        public async Task<IActionResult> SaveActions([FromBody] SaveActionsRequest request)
        {
            try
            {
                var monthYearKey = ConvertThaiMonthToKey(request.MonthYear);

                var now = _dateTimeProvider.Now;
                var currentMonthKey = now.ToString("yyyy-MM");

                // ป้องกันการบันทึกรายการในเดือนที่ยังมาไม่ถึง หรือเดือนในอดีตที่ปิดยอดไปแล้ว
                if (string.Compare(monthYearKey, currentMonthKey) > 0)
                {
                    return Json(new { success = false, error = "ไม่สามารถบันทึกรายการในเดือนที่ยังมาไม่ถึงได้" });
                }

                if (string.Compare(monthYearKey, currentMonthKey) < 0)
                {
                    return Json(new
                    {
                        success = false,
                        error = "งวดบัญชีนี้ปิดยอดเรียบร้อยแล้ว (อ่านอย่างเดียว) ไม่สามารถแก้ไขหรือบันทึกได้"
                    });
                }

                // ลบรายการเดิมที่เคยบันทึกไว้ในเดือนนี้ออกก่อน เพื่อป้องกันรายการซ้ำซ้อน
                var incomingOrderIds = request.Actions.Select(a => a.OrderId).ToList();
                if (incomingOrderIds.Any())
                {
                    var existingActionsToClear = await _context.MonthlyOrderActions
                        .Where(a => a.MonthYear == monthYearKey && incomingOrderIds.Contains(a.OrderTrackingMasterId))
                        .ToListAsync();
                    if (existingActionsToClear.Any())
                    {
                        _context.MonthlyOrderActions.RemoveRange(existingActionsToClear);
                        await _context.SaveChangesAsync();
                    }
                }

                // ดึงรายการ pending orders ทั้งหมดในเดือนนี้
                var existingActions = await _context.MonthlyOrderActions
                    .Where(moa => moa.MonthYear == monthYearKey)
                    .Select(moa => moa.OrderTrackingMasterId)
                    .ToListAsync();

                var priorActions = await _context.MonthlyOrderActions
                    .Where(moa => string.Compare(moa.MonthYear, monthYearKey) < 0)
                    .OrderByDescending(moa => moa.MonthYear)
                    .ThenByDescending(moa => moa.CreatedAt)
                    .ToListAsync();

                var latestPriorActionMap = priorActions
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key, g => g.First());

                // หา IDs ที่ถูกเลือก (ส่งมาใน request)
                var selectedOrderIds = request.Actions.Select(a => a.OrderId).ToHashSet();

                // ใช้ list ที่ส่งมาจาก frontend (รายการ pending ทั้งหมดที่แสดงในหน้า)
                HashSet<Guid> allPendingIds;

                if (request.AllPendingOrderIds != null && request.AllPendingOrderIds.Count > 0)
                {
                    // ใช้ list จาก frontend ← วิธีนี้แม่นยำที่สุด
                    allPendingIds = request.AllPendingOrderIds.ToHashSet();
                    Console.WriteLine($"Using AllPendingOrderIds from frontend: {allPendingIds.Count}");
                }
                else
                {
                    // Fallback: หาจาก database โดยใช้ ApprovedDate (เหมือนกับ Detail method)
                    var carryOverIds = latestPriorActionMap
                        .Where(kvp =>
                        {
                            if (kvp.Value.Action == "Deferred")
                            {
                                var pp = kvp.Value.MonthYear.Split('-');
                                var tp = monthYearKey.Split('-');
                                if (pp.Length == 2 && tp.Length == 2
                                                   && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                                   && int.TryParse(tp[0], out var tY) &&
                                                   int.TryParse(tp[1], out var tM))
                                {
                                    return ((tY - pY) * 12) + (tM - pM) == 1;
                                }
                            }
                            else if (kvp.Value.Action == "Skipped")
                            {
                                var pp = kvp.Value.MonthYear.Split('-');
                                var tp = monthYearKey.Split('-');
                                if (pp.Length == 2 && tp.Length == 2
                                                   && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                                   && int.TryParse(tp[0], out var tY) &&
                                                   int.TryParse(tp[1], out var tM))
                                {
                                    return ((tY - pY) * 12) + (tM - pM) >= 1;
                                }
                            }

                            return false;
                        })
                        .Select(kvp => kvp.Key)
                        .ToHashSet();

                    var keyParts2 = monthYearKey.Split('-');
                    DateTime monthStart2 = DateTime.MinValue, monthEnd2 = DateTime.MaxValue;
                    if (keyParts2.Length == 2 &&
                        int.TryParse(keyParts2[0], out var kYear2) &&
                        int.TryParse(keyParts2[1], out var kMonth2))
                    {
                        monthStart2 = new DateTime(kYear2, kMonth2, 1, 0, 0, 0, DateTimeKind.Utc);
                        monthEnd2 = monthStart2.AddMonths(1);
                    }

                    // ดึง orders ทั้งหมดที่ยังไม่ได้ดำเนินการ แล้ว filter ด้วย ApprovedDate ใน memory
                    var allOrders = await _context.OrderTrackingMasters
                        .Include(o => o.Report)
                        .Where(o => !existingActions.Contains(o.Id))
                        .ToListAsync();

                    var monthOrderIds = allOrders
                        .Where(o =>
                        {
                            var approvedDate = ParseThaiDate(o.ApprovedDate);
                            if (approvedDate == null) return false;
                            return approvedDate.Value >= monthStart2 && approvedDate.Value < monthEnd2;
                        })
                        .Select(o => o.Id)
                        .ToHashSet();

                    allPendingIds = carryOverIds.Union(monthOrderIds).ToHashSet();
                    Console.WriteLine(
                        $"Fallback - Carry-over: {carryOverIds.Count}, Month Orders: {monthOrderIds.Count}");
                }

                // สินค้าที่ไม่ได้เลือก = pending ทั้งหมด - สินค้าที่เลือก
                var unselectedIds = allPendingIds.Except(selectedOrderIds).ToList();

                // Debug logging
                Console.WriteLine($"=== SaveActions Debug ===");
                Console.WriteLine($"Month: {monthYearKey}");
                Console.WriteLine($"Existing Actions: {existingActions.Count}");
                Console.WriteLine($"All Pending: {allPendingIds.Count}");
                Console.WriteLine($"Selected: {selectedOrderIds.Count}");
                Console.WriteLine($"Unselected (auto-skip): {unselectedIds.Count}");
                Console.WriteLine($"Selected Order IDs: {string.Join(", ", selectedOrderIds)}");
                if (unselectedIds.Any())
                    Console.WriteLine($"Unselected Order IDs: {string.Join(", ", unselectedIds)}");
                Console.WriteLine($"========================");
                Console.WriteLine($"========================");

                // บันทึกสินค้าที่เลือก
                foreach (var action in request.Actions)
                {
                    latestPriorActionMap.TryGetValue(action.OrderId, out var priorAction);

                    decimal originalPrice = action.Price;
                    if (action.Action == "Deferred")
                    {
                        // Bug Fix: ลำดับความสำคัญราคาสำหรับ Deferred:
                        // 1. ใช้ ActionPrice จาก prior Deferred ก่อน (Inherit ราคาที่ตกลงไว้)
                        // 2. ถ้าไม่มี ใช้ราคาจาก OrderTrackingMaster.Amount
                        if (priorAction?.Action == "Deferred" && priorAction.ActionPrice > 0)
                        {
                            originalPrice = priorAction.ActionPrice;
                        }
                        else if (priorAction?.Action == "Skipped")
                        {
                            // ถ้า Skipped กลางทาง ให้ย้อนหา Deferred เดิมสุด
                            var earlierDeferred = priorActions
                                .Where(a => a.OrderTrackingMasterId == action.OrderId && a.Action == "Deferred")
                                .OrderByDescending(a => a.MonthYear)
                                .ThenByDescending(a => a.CreatedAt)
                                .FirstOrDefault();
                            if (earlierDeferred != null && earlierDeferred.ActionPrice > 0)
                            {
                                originalPrice = earlierDeferred.ActionPrice;
                            }
                        }
                        else
                        {
                            // Fallback: ดึงจาก OrderTrackingMaster.Amount
                            var otm = await _context.OrderTrackingMasters
                                .Where(o => o.Id == action.OrderId)
                                .Select(o => o.Amount)
                                .FirstOrDefaultAsync();
                            if (!string.IsNullOrEmpty(otm) && decimal.TryParse(otm, out var realPrice))
                            {
                                originalPrice = realPrice;
                            }
                        }
                    }

                    var monthlyAction = new MonthlyOrderAction
                    {
                        Id = Guid.NewGuid(),
                        OrderTrackingMasterId = action.OrderId,
                        MonthYear = monthYearKey,
                        Action = action.Action,
                        ActionPrice = action.Action == "Deferred" ? originalPrice
                            : action.Action == "Skipped" ? 0
                            : action.Price,
                        IsForcedPayment = false,
                        DeferredFromMonth = (action.Action == "Deferred" && priorAction != null) ? priorAction.MonthYear : null,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.MonthlyOrderActions.Add(monthlyAction);
                }

                // หากเป็นการปิดยอดเดือน (IsCloseMonth = true) ให้บันทึกสินค้าที่ไม่ได้เลือกเป็น "Skipped" อัตโนมัติ เพื่อยกยอดไปเดือนถัดไป
                if (request.IsCloseMonth)
                {
                    foreach (var unselectedId in unselectedIds)
                    {
                        var monthlyAction = new MonthlyOrderAction
                        {
                            Id = Guid.NewGuid(),
                            OrderTrackingMasterId = unselectedId,
                            MonthYear = monthYearKey,
                            Action = "Skipped",
                            ActionPrice = 0,
                            IsForcedPayment = false,
                            DeferredFromMonth = null,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };

                        _context.MonthlyOrderActions.Add(monthlyAction);
                    }
                }

                await _context.SaveChangesAsync();

                // ==========================================
                // ส่งข้อมูลไปสำรองใน Google Sheets (9 คอลัมน์ ผ่าน MonthlyOrderSyncService)
                // ==========================================
                try
                {
                    await _syncService.SyncSpecificMonthsToGoogleSheetsAsync(
                        new List<string> { monthYearKey },
                        reason: $"User กดปุ่ม 'บันทึก' ในหน้ารายละเอียดเดือน {monthYearKey} (บันทึก {request.Actions.Count} รายการ)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending backup to Google Sheets: {ex.Message}");
                }
                // ==========================================
                // ==========================================

                await _hubContext.Clients.All.SendAsync("ReceiveMonthlyCostUpdate");
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CancelAction([FromBody] RevertActionRequest request)
        {
            return await RevertAction(request);
        }

        [HttpPost]
        public async Task<IActionResult> BulkCancelActions([FromBody] BulkRevertActionRequest request)
        {
            return await BulkRevertAction(request);
        }

        // GET: /MonthlyCost/GetDetailStats?monthYear=มกราคม+2569 — API สำหรับ polling realtime ใน Detail
        [HttpGet]
        public async Task<IActionResult> GetDetailStats(string monthYear)
        {
            if (string.IsNullOrEmpty(monthYear))
                return BadRequest(new { error = "monthYear is required" });

            var monthYearKey = ConvertThaiMonthToKey(monthYear);

            var existingActions = await _context.MonthlyOrderActions
                .Where(moa => moa.MonthYear == monthYearKey)
                .ToListAsync();

            var priorActions = await _context.MonthlyOrderActions
                .Where(moa => string.Compare(moa.MonthYear, monthYearKey) < 0)
                .OrderByDescending(moa => moa.MonthYear)
                .ThenByDescending(moa => moa.CreatedAt)
                .ToListAsync();

            var latestPriorActionMap = priorActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .ToDictionary(g => g.Key, g => g.First());

            var existingActionIds = existingActions.Select(ea => ea.OrderTrackingMasterId).ToHashSet();

            var keyParts = monthYearKey.Split('-');
            DateTime monthStart = DateTime.MinValue, monthEnd = DateTime.MaxValue;
            if (keyParts.Length == 2 &&
                int.TryParse(keyParts[0], out var kYear) &&
                int.TryParse(keyParts[1], out var kMonth))
            {
                monthStart = new DateTime(kYear, kMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                monthEnd = monthStart.AddMonths(1);
            }

            var now = _dateTimeProvider.Now;
            var currentMonthKey = now.ToString("yyyy-MM");
            var isPastMonth = string.Compare(monthYearKey, currentMonthKey) < 0;

            var pendingOrders = await _context.OrderTrackingMasters
                .Where(otm => !existingActionIds.Contains(otm.Id))
                .ToListAsync();

            pendingOrders = pendingOrders
                .Where(otm =>
                {
                    if (latestPriorActionMap.TryGetValue(otm.Id, out var prior) && prior.Action == "ReceivedFull")
                        return false;
                    var approvedDate = ParseThaiDate(otm.ApprovedDate);
                    if (approvedDate == null) return false;
                    return approvedDate.Value < monthEnd;
                })
                .ToList();

            if (isPastMonth)
            {
                // เดือนอดีต pendingOrders จะย้ายไปเป็น auto-skipped ใน savedActions
                pendingOrders.Clear();
            }

            var pendingAmount = pendingOrders.Sum(o =>
            {
                if (latestPriorActionMap.TryGetValue(o.Id, out var priorAction) && priorAction.Action == "Deferred" &&
                    priorAction.ActionPrice > 0)
                    return priorAction.ActionPrice;
                return decimal.TryParse(o.Amount, out var a) ? a : 0m;
            });

            var receivedActions = existingActions.Where(a => a.Action == "ReceivedFull").ToList();
            var deferredActions = existingActions.Where(a => a.Action == "Deferred").ToList();
            var skippedActions = existingActions.Where(a => a.Action == "Skipped").ToList();

            var skippedIds = skippedActions.Select(a => a.OrderTrackingMasterId).ToList();
            var skippedAmounts = await _context.OrderTrackingMasters
                .Where(o => skippedIds.Contains(o.Id))
                .Select(o => new { o.Id, o.Amount })
                .ToListAsync();
            var skippedAmountMap = skippedAmounts
                .ToDictionary(o => o.Id, o => decimal.TryParse(o.Amount, out var a) ? a : 0m);

            var processedAmount = receivedActions.Sum(a => a.ActionPrice);

            var totalPlanned = existingActions.Sum(a =>
            {
                if (a.Action == "ReceivedFull" || a.Action == "Deferred") return a.ActionPrice;
                if (skippedAmountMap.TryGetValue(a.OrderTrackingMasterId, out var amt)) return amt;
                return 0m;
            }) + pendingAmount;

            var remainingAmount = Math.Max(0m, totalPlanned - processedAmount);

            return Json(new
            {
                totalOrders = existingActions.Count + pendingOrders.Count,
                savedCount = existingActions.Count,
                pendingCount = pendingOrders.Count,
                receivedAmount = processedAmount,
                processedAmount,
                totalPlanned,
                remainingAmount
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetAllPendingOrders(string monthYear)
        {
            try
            {
                if (string.IsNullOrEmpty(monthYear))
                {
                    return BadRequest(new { error = "monthYear parameter is required" });
                }

                var monthYearKey = ConvertThaiMonthToKey(monthYear);

                var existingActions = await _context.MonthlyOrderActions
                    .Where(moa => moa.MonthYear == monthYearKey)
                    .ToListAsync();

                var existingActionIds = existingActions.Select(ea => ea.OrderTrackingMasterId).ToHashSet();

                var priorActions = await _context.MonthlyOrderActions
                    .Where(moa => string.Compare(moa.MonthYear, monthYearKey) < 0)
                    .OrderByDescending(moa => moa.MonthYear)
                    .ThenByDescending(moa => moa.CreatedAt)
                    .ToListAsync();

                var latestPriorActionMap = priorActions
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key, g => g.First());

                var kp = monthYearKey.Split('-');
                DateTime gMonthStart = DateTime.MinValue, gMonthEnd = DateTime.MaxValue;
                if (kp.Length == 2 &&
                    int.TryParse(kp[0], out var gYear) &&
                    int.TryParse(kp[1], out var gMonth))
                {
                    gMonthStart = new DateTime(gYear, gMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                    gMonthEnd = gMonthStart.AddMonths(1);
                }

                var now = _dateTimeProvider.Now;
                var currentMonthKey = now.ToString("yyyy-MM");
                var isPastMonth = string.Compare(monthYearKey, currentMonthKey) < 0;

                if (isPastMonth)
                {
                    // สำหรับเดือนอดีต รายการ pending ถูกย้ายไปบันทึกแล้ว (auto-skipped) หมดแล้ว
                    return Ok(new List<object>());
                }

                var allPendingOrders = await _context.OrderTrackingMasters
                    .Include(otm => otm.Report)
                    .Include(otm => otm.MatchedInWeeklyPlans)
                    .ThenInclude(wpd => wpd.WeeklyPlan)
                    .Where(otm => !existingActionIds.Contains(otm.Id))
                    .OrderBy(otm => otm.PoNumber)
                    .ToListAsync();

                allPendingOrders = allPendingOrders
                    .Where(otm =>
                    {
                        if (latestPriorActionMap.TryGetValue(otm.Id, out var prior) && prior.Action == "ReceivedFull")
                            return false;
                        var approvedDate = ParseThaiDate(otm.ApprovedDate);
                        if (approvedDate == null) return false;
                        return approvedDate.Value < gMonthEnd;
                    })
                    .ToList();

                var result = new List<object>();
                foreach (var otm in allPendingOrders)
                {
                    if (latestPriorActionMap.TryGetValue(otm.Id, out var priorAction))
                    {
                        if (priorAction.Action == "ReceivedFull") continue;
                    }

                    var quantity = 1;
                    if (!string.IsNullOrEmpty(otm.RemarksQuantity) && int.TryParse(otm.RemarksQuantity, out var qty))
                    {
                        quantity = qty;
                    }

                    var amount = 0m;
                    if (!string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var amt))
                    {
                        amount = amt;
                    }

                    var forwardedStatus = string.Empty;
                    var forwardedFromMonth = string.Empty;

                    if (priorAction != null)
                    {
                        if (priorAction.Action == "Deferred")
                        {
                            forwardedStatus = "Deferred";
                            forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                            // Bug Fix: Inherit ActionPrice จาก Deferred record
                            if (priorAction.ActionPrice > 0)
                            {
                                amount = priorAction.ActionPrice;
                            }
                        }
                        else if (priorAction.Action == "Skipped")
                        {
                            forwardedStatus = "Skipped";
                            forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                            // Bug Fix: ถ้า Skipped แต่เคยมี Deferred ก่อนหน้า ให้ดึง ActionPrice จาก Deferred
                            var earlierDeferred = priorActions
                                .Where(a => a.OrderTrackingMasterId == otm.Id && a.Action == "Deferred")
                                .OrderByDescending(a => a.MonthYear)
                                .ThenByDescending(a => a.CreatedAt)
                                .FirstOrDefault();
                            if (earlierDeferred != null && earlierDeferred.ActionPrice > 0)
                            {
                                amount = earlierDeferred.ActionPrice;
                            }
                        }
                    }

                    var productName = otm.Remarks ?? "ไม่ระบุ";

                    var latestMatchedPlan = otm.MatchedInWeeklyPlans
                        .OrderByDescending(w => w.WeeklyPlan.UploadedAt)
                        .FirstOrDefault();

                    var targetDelivery = !string.IsNullOrWhiteSpace(latestMatchedPlan?.DeliveryTarget)
                        ? latestMatchedPlan.DeliveryTarget
                        : "-";
                    var department = !string.IsNullOrEmpty(latestMatchedPlan?.Department)
                        ? latestMatchedPlan.Department
                        : (!string.IsNullOrEmpty(otm.Urgency) ? otm.Urgency : "-");

                    result.Add(new
                    {
                        orderId = otm.Id.ToString(),
                        po = otm.PoNumber ?? "",
                        name = productName,
                        qty = quantity,
                        price = amount,
                        delivery = targetDelivery,
                        dept = department,
                        forwardedStatus = forwardedStatus,
                        forwardedFromMonth = forwardedFromMonth,
                        originalReportName = otm.Report?.ReportName ?? "N/A"
                    });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                // Log the error for debugging
                Console.WriteLine($"GetAllPendingOrders Error: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RevertAction([FromBody] RevertActionRequest request)
        {
            try
            {
                var action = await _context.MonthlyOrderActions.FindAsync(request.ActionId);
                if (action != null)
                {
                    var now = _dateTimeProvider.Now;
                    var currentMonthKey = now.ToString("yyyy-MM");
                    if (action.MonthYear != currentMonthKey)
                    {
                        var totalActionsInMonth =
                            await _context.MonthlyOrderActions.CountAsync(moa => moa.MonthYear == action.MonthYear);
                        var priorReceivedCount = await _context.MonthlyOrderActions
                            .Where(moa =>
                                string.Compare(moa.MonthYear, action.MonthYear) < 0 && moa.Action == "ReceivedFull")
                            .Select(moa => moa.OrderTrackingMasterId)
                            .Distinct()
                            .CountAsync();
                        var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();
                        if (totalOrdersEver - priorReceivedCount - totalActionsInMonth <= 0)
                        {
                            return BadRequest(new
                            {
                                success = false,
                                error = "งวดบัญชีนี้ปิดยอด (Completed) เรียบร้อยแล้ว ไม่สามารถย้อนคืนค่าได้"
                            });
                        }
                    }

                    _context.MonthlyOrderActions.Remove(action);
                    await _context.SaveChangesAsync();

                    /* ==========================================
                    // ส่วนที่เพิ่ม: ส่งข้อมูลแจ้งลบ/ยกเลิก (Revert) ไปยัง Google Sheets
                    // ==========================================
                    try
                    {
                        string? appScriptUrl = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
                        if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                        {
                            var sheetPayload = new
                            {
                                ActionType = "Revert", // บอกสคริปต์ว่านี่คือการยกเลิก
                                Timestamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                                RevertedActionId = action.Id,
                                OrderId = action.OrderTrackingMasterId,
                                MonthYear = action.MonthYear
                            };

                            var client = _httpClientFactory.CreateClient();
                            client.Timeout = TimeSpan.FromSeconds(30);
                            var jsonString = System.Text.Json.JsonSerializer.Serialize(sheetPayload);
                            var content = new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                            await client.PostAsync(appScriptUrl, content);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending Revert to Google Sheets: {ex.Message}");
                    }
                    // ==========================================
                    */
                    await _hubContext.Clients.All.SendAsync("ReceiveMonthlyCostUpdate");
                }

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> BulkRevertAction([FromBody] BulkRevertActionRequest request)
        {
            try
            {
                if (request.ActionIds != null && request.ActionIds.Any())
                {
                    var actions = await _context.MonthlyOrderActions
                        .Where(a => request.ActionIds.Contains(a.Id))
                        .ToListAsync();

                    if (actions.Any())
                    {
                        var firstMonth = actions.First().MonthYear;
                        var now = _dateTimeProvider.Now;
                        var currentMonthKey = now.ToString("yyyy-MM");
                        if (firstMonth != currentMonthKey)
                        {
                            var totalActionsInMonth =
                                await _context.MonthlyOrderActions.CountAsync(moa => moa.MonthYear == firstMonth);
                            var priorReceivedCount = await _context.MonthlyOrderActions
                                .Where(moa =>
                                    string.Compare(moa.MonthYear, firstMonth) < 0 && moa.Action == "ReceivedFull")
                                .Select(moa => moa.OrderTrackingMasterId)
                                .Distinct()
                                .CountAsync();
                            var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();
                            if (totalOrdersEver - priorReceivedCount - totalActionsInMonth <= 0)
                            {
                                return BadRequest(new
                                {
                                    success = false,
                                    error = "งวดบัญชีนี้ปิดยอด (Completed) เรียบร้อยแล้ว ไม่สามารถย้อนคืนค่าได้"
                                });
                            }
                        }

                        _context.MonthlyOrderActions.RemoveRange(actions);
                        await _context.SaveChangesAsync();

                        /* ==========================================
                        // ส่วนที่เพิ่ม: ส่งข้อมูลแจ้งลบ/ยกเลิกแบบกลุ่ม (BulkRevert) ไปยัง Google Sheets
                        // ==========================================
                        try
                        {
                            string? appScriptUrl = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
                            if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                            {
                                var sheetPayload = new
                                {
                                    ActionType = "BulkRevert", // บอกสคริปต์ว่านี่คือการยกเลิกแบบกลุ่ม
                                    Timestamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                                    RevertedActionIds = actions.Select(a => a.Id).ToList(),
                                    OrderIds = actions.Select(a => a.OrderTrackingMasterId).ToList()
                                };

                                var client = _httpClientFactory.CreateClient();
                                client.Timeout = TimeSpan.FromSeconds(30);
                                var jsonString = System.Text.Json.JsonSerializer.Serialize(sheetPayload);
                                var content = new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                                await client.PostAsync(appScriptUrl, content);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error sending BulkRevert to Google Sheets: {ex.Message}");
                        }
                        // ==========================================
                        */
                        await _hubContext.Clients.All.SendAsync("ReceiveMonthlyCostUpdate");
                    }
                }

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        // GET: /MonthlyCost/Summary
        public async Task<IActionResult> Summary(int? year)
        {
            ViewData["HeaderTitle"] = "รายการสั่งซื้อประจำปี";

            var thaiMonths = new[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            var now = _dateTimeProvider.Now;
            var selectedYear = year ?? now.Year;

            // ── Available Years ──
            var dbActionYears = await _context.MonthlyOrderActions
                .Where(a => !string.IsNullOrEmpty(a.MonthYear) && a.MonthYear.Length >= 4)
                .Select(a => a.MonthYear.Substring(0, 4))
                .Distinct()
                .ToListAsync();

            var parsedActionYears = dbActionYears
                .Select(y => int.TryParse(y, out var yr) ? yr : (int?)null)
                .Where(y => y.HasValue)
                .Select(y => y!.Value);

            var approvedDateYears = (await _context.OrderTrackingMasters
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .Select(o => o.ApprovedDate)
                    .ToListAsync())
                .Select(d => ParseThaiDate(d))
                .Where(d => d.HasValue)
                .Select(d => d!.Value.Year);

            var availableYears = parsedActionYears
                .Concat(approvedDateYears)
                .Concat(new[] { now.Year, selectedYear })
                .Distinct()
                .OrderByDescending(y => y)
                .ToList();

            // ── Query all actions for selected year ──
            var yearPrefix = $"{selectedYear:0000}-";

            var allActions = await _context.MonthlyOrderActions
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .Where(a => a.MonthYear.StartsWith(yearPrefix))
                .ToListAsync();

            // Deduplicate: 1 order ให้แสดงแค่บรรทัดเดียว (เอา action ล่าสุดของ order นั้นมาเป็นสถานะปัจจุบัน)
            var deduplicated = allActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .Select(g => g.OrderByDescending(a => a.CreatedAt).First())
                .ToList();

            // ── Map to YearlyOrderItem ──
            var items = new List<YearlyOrderItem>();

            foreach (var act in deduplicated)
            {
                var otm = act.OrderTrackingMaster;
                if (otm == null) continue;

                var latestPlan = otm.MatchedInWeeklyPlans
                    .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                    .FirstOrDefault();

                string orderName = !string.IsNullOrWhiteSpace(otm.Remarks)
                    ? otm.Remarks.Replace("สั่งทำ ", "").Replace("สั่งทำ", "").Trim()
                    : "ไม่ระบุ";

                string dept = latestPlan?.Department ?? otm.Urgency ?? "-";

                decimal amount = 0m;
                if (act.Action == "Deferred" || act.Action == "ReceivedFull")
                {
                    amount = act.ActionPrice > 0 ? act.ActionPrice : 0m;
                }
                if (amount == 0 && !string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var parsed))
                {
                    amount = parsed;
                }

                string actionDisplay = act.Action switch
                {
                    "ReceivedFull" => "รับสินค้าแล้ว",
                    "Deferred" => "ผ่อนชำระ",
                    "Skipped" => "ยังไม่รับสินค้า",
                    _ => act.Action
                };

                var normalizedKey = act.MonthYear.Length > 7 ? act.MonthYear.Substring(0, 7) : act.MonthYear;
                var keyParts = normalizedKey.Split('-');
                int monthNum = keyParts.Length == 2 && int.TryParse(keyParts[1], out var mn) ? mn : 0;
                int yearNum = keyParts.Length >= 1 && int.TryParse(keyParts[0], out var yn) ? yn : selectedYear;
                string monthDisplay = monthNum >= 1 && monthNum <= 12
                    ? $"{thaiMonths[monthNum]} {yearNum + 543}"
                    : normalizedKey;

                string qtyDisplay = !string.IsNullOrWhiteSpace(otm.RemarksQuantity)
                    ? (otm.RemarksQuantity.Trim().EndsWith("ชิ้น") ? otm.RemarksQuantity.Trim() : $"{otm.RemarksQuantity.Trim()} ชิ้น")
                    : "-";

                var approvedDt = ParseThaiDate(otm.ApprovedDate);
                string approvedMonthDisplay = "-";
                int approvedMonthNum = 0;
                if (approvedDt.HasValue)
                {
                    approvedMonthNum = approvedDt.Value.Month;
                    approvedMonthDisplay = approvedMonthNum >= 1 && approvedMonthNum <= 12
                        ? $"{thaiMonths[approvedMonthNum]} {approvedDt.Value.Year + 543}"
                        : "-";
                }

                items.Add(new YearlyOrderItem
                {
                    ActionId = act.Id,
                    OrderId = otm.Id,
                    PoNumber = otm.PoNumber ?? "-",
                    OrderName = orderName,
                    Quantity = qtyDisplay,
                    Department = dept,
                    Amount = amount,
                    MonthKey = normalizedKey,
                    MonthDisplay = monthDisplay,
                    MonthNumber = monthNum,
                    Action = act.Action,
                    ActionDisplay = actionDisplay,
                    ApprovedDate = otm.ApprovedDate ?? "-",
                    ApprovedMonthDisplay = approvedMonthDisplay,
                    ApprovedMonthNumber = approvedMonthNum,
                    CreatedAt = act.CreatedAt
                });
            }

            // ── KPI (จากข้อมูลทั้งหมดก่อน filter) ──
            var vm = new YearlyOrderSummaryViewModel
            {
                SelectedYear = selectedYear,
                AvailableYears = availableYears,
                Items = items,
                TotalOrders = items.Count,
                TotalReceivedCount = items.Count(i => i.Action == "ReceivedFull"),
                TotalReceivedAmount = items.Where(i => i.Action == "ReceivedFull").Sum(i => i.Amount),
                TotalDeferredCount = items.Count(i => i.Action == "Deferred"),
                TotalDeferredAmount = items.Where(i => i.Action == "Deferred").Sum(i => i.Amount),
                TotalSkippedCount = items.Count(i => i.Action == "Skipped"),
                TotalSkippedAmount = items.Where(i => i.Action == "Skipped").Sum(i => i.Amount)
            };

            return View(vm);
        }

        private DateTime? ParseThaiDate(string? thaiDateStr)
        {
            if (string.IsNullOrWhiteSpace(thaiDateStr)) return null;

            // Remove extra spaces
            thaiDateStr = thaiDateStr.Trim();

            // Ignore standard placeholder '-' or empty/whitespace values
            if (thaiDateStr == "-") return null;

            // If contains time (space), take only date part
            if (thaiDateStr.Contains(' '))
            {
                thaiDateStr = thaiDateStr.Split(' ')[0];
            }

            // Normalize separators (hyphens to slashes)
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

                // Handle yyyy/MM/dd format
                if (p1 > 1000)
                {
                    year = p1;
                    month = p2;
                    day = p3;
                }
                // Handle MM/dd/yyyy format (month and day are swapped)
                else if (p2 > 12 && p1 <= 12)
                {
                    day = p2;
                    month = p1;
                    year = p3;
                }

                // Convert Buddhist year to Gregorian if year > 2500
                if (year > 2500) year -= 543;

                try
                {
                    var parsed = new DateTime(year, month, day);
                    return parsed;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        // POST: /MonthlyCost/ExportDetailExcel
        [HttpPost]
        public async Task<IActionResult> ExportDetailExcel([FromForm] string monthYear, [FromForm] string? actionFilter = "all", [FromForm] string? statusFilter = "all", [FromForm] string? draftCartJson = null)
        {
            if (string.IsNullOrEmpty(monthYear))
                return BadRequest("monthYear parameter is required");

            var monthYearKey = ConvertThaiMonthToKey(monthYear);
            var canonicalThaiMonth = ConvertKeyToThaiMonth(monthYearKey);

            var draftCart = new Dictionary<Guid, DraftCartItem>();
            if (!string.IsNullOrEmpty(draftCartJson))
            {
                try 
                { 
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DraftCartItem>>(draftCartJson);
                    if (parsed != null)
                    {
                        foreach (var kvp in parsed)
                        {
                            if (Guid.TryParse(kvp.Key, out Guid id))
                                draftCart[id] = kvp.Value;
                        }
                    }
                } 
                catch { }
            }

            // Register Mock Graphic Engine for ClosedXML
            ClosedXML.Excel.LoadOptions.DefaultGraphicEngine = new MockGraphicEngine();

            // 1. ดึงรายการที่บันทึกแล้วในเดือนนี้
            var existingActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .ThenInclude(o => o.MatchedInWeeklyPlans)
                .ThenInclude(w => w.WeeklyPlan)
                .Where(moa => moa.MonthYear == monthYearKey)
                .OrderByDescending(moa => moa.CreatedAt)
                .ToListAsync();

            // 2. ดึงรายการในเดือนก่อนหน้าเพื่อหาตัวที่ผ่อนหรือค้างยกยอดมา
            var priorActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .ThenInclude(o => o.MatchedInWeeklyPlans)
                .ThenInclude(w => w.WeeklyPlan)
                .Where(moa => string.Compare(moa.MonthYear, monthYearKey) < 0)
                .OrderByDescending(moa => moa.MonthYear)
                .ThenByDescending(moa => moa.CreatedAt)
                .ToListAsync();

            var latestPriorActionMap = priorActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .ToDictionary(g => g.Key, g => g.First());

            var existingActionIds = existingActions.Select(ea => ea.OrderTrackingMasterId).ToHashSet();

            // Parse month date range
            var keyParts = monthYearKey.Split('-');
            DateTime monthStart = DateTime.MinValue, monthEnd = DateTime.MaxValue;
            if (keyParts.Length == 2 &&
                int.TryParse(keyParts[0], out var kYear) &&
                int.TryParse(keyParts[1], out var kMonth))
            {
                monthStart = new DateTime(kYear, kMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                monthEnd = monthStart.AddMonths(1);
            }

            // 3. ดึงรายการรอดำเนินการในเดือนนี้
            var pendingOrdersRaw = await _context.OrderTrackingMasters
                .Include(o => o.MatchedInWeeklyPlans)
                .ThenInclude(w => w.WeeklyPlan)
                .Where(otm => !existingActionIds.Contains(otm.Id))
                .OrderBy(otm => otm.PoNumber)
                .ToListAsync();

            var pendingOrders = pendingOrdersRaw
                .Where(otm =>
                {
                    if (latestPriorActionMap.TryGetValue(otm.Id, out var prior) && prior.Action == "ReceivedFull")
                        return false;

                    var approvedDate = ParseThaiDate(otm.ApprovedDate);
                    if (approvedDate == null) return false;

                    return approvedDate.Value < monthEnd;
                })
                .ToList();

            // 4. รวบรวมข้อมูลสำหรับ Export
            var exportRows = new List<ExcelExportRowDto>();

            // A. รายการที่บันทึกแล้ว (Saved)
            if (actionFilter != "DraftOnly" && (statusFilter == "all" || statusFilter == "saved"))
            {
                foreach (var action in existingActions)
                {
                    var otm = action.OrderTrackingMaster;
                    var latestPlan = otm?.MatchedInWeeklyPlans?.OrderByDescending(w => w.WeeklyPlan?.UploadedAt).FirstOrDefault();

                    string sourceText = "สั่งผลิตประจำเดือนนี้";
                    if (otm != null && latestPriorActionMap.TryGetValue(otm.Id, out var prior))
                    {
                        if (prior.Action == "Deferred")
                            sourceText = $"ผ่อนยกยอดมาจาก {ConvertKeyToThaiMonth(prior.MonthYear)}";
                        else if (prior.Action == "Skipped")
                            sourceText = $"ค้างมาจาก {ConvertKeyToThaiMonth(prior.MonthYear)}";
                    }

                    string actionLabel = action.Action switch
                    {
                        "ReceivedFull" => "รับสินค้าแล้ว",
                        "Deferred" => "ผ่อนชำระ",
                        "Skipped" => "ยังไม่รับสินค้า",
                        _ => action.Action
                    };

                    decimal amount = action.ActionPrice > 0 ? action.ActionPrice : ParseDecimal(otm?.Amount);

                    exportRows.Add(new ExcelExportRowDto
                    {
                        PoNumber = otm?.PoNumber ?? "-",
                        ProductName = otm?.Remarks ?? "-",
                        Department = !string.IsNullOrEmpty(latestPlan?.Department) ? latestPlan.Department : (!string.IsNullOrEmpty(otm?.Urgency) ? otm.Urgency : "-"),
                        Urgency = !string.IsNullOrEmpty(otm?.Urgency) ? otm.Urgency : "-",
                        DeliveryTarget = !string.IsNullOrEmpty(latestPlan?.DeliveryTarget) ? latestPlan.DeliveryTarget : "-",
                        Source = sourceText,
                        ActionType = action.Action,
                        ActionLabel = actionLabel,
                        Status = "บันทึกแล้ว",
                        Quantity = !string.IsNullOrEmpty(otm?.RemarksQuantity) ? otm.RemarksQuantity : "1",
                        Amount = amount,
                        DateText = action.CreatedAt.ToString("dd/MM/yyyy HH:mm")
                    });
                }
            }

            // B. รายการรอดำเนินการ (Pending)
            if (statusFilter == "all" || statusFilter == "pending")
            {
                foreach (var otm in pendingOrders)
                {
                    if (actionFilter == "DraftOnly" && !draftCart.ContainsKey(otm.Id))
                    {
                        continue; // ข้ามรายการที่ไม่ได้อยู่ในตะกร้าจำลอง
                    }
                    var latestPlan = otm.MatchedInWeeklyPlans?.OrderByDescending(w => w.WeeklyPlan?.UploadedAt).FirstOrDefault();

                    string sourceText = "สั่งผลิตประจำเดือนนี้";
                    string actionType = "Pending";
                    string actionLabel = "รอดำเนินการ";

                    if (latestPriorActionMap.TryGetValue(otm.Id, out var prior))
                    {
                        if (prior.Action == "Deferred")
                        {
                            sourceText = $"ผ่อนยกยอดมาจาก {ConvertKeyToThaiMonth(prior.MonthYear)}";
                            actionType = "Deferred";
                            actionLabel = "ผ่อนชำระ (รอยืนยัน)";
                        }
                        else if (prior.Action == "Skipped")
                        {
                            sourceText = $"ค้างมาจาก {ConvertKeyToThaiMonth(prior.MonthYear)}";
                            actionType = "Skipped";
                            actionLabel = "ยังไม่รับสินค้า (รอยืนยัน)";
                        }
                    }

                    decimal amount = ParseDecimal(otm.Amount);
                    string statusText = "รอดำเนินการ";

                    // OVERLAY DRAFT CART
                    if (draftCart.TryGetValue(otm.Id, out var draft))
                    {
                        actionType = draft.action;
                        actionLabel = draft.action switch
                        {
                            "ReceivedFull" => "รับสินค้าแล้ว",
                            "Deferred" => "ผ่อนชำระ",
                            "Skipped" => "ยังไม่รับสินค้า",
                            _ => draft.action
                        };
                        amount = draft.actionPrice > 0 ? draft.actionPrice : amount;
                        statusText = "บันทึกแล้ว (จำลอง)";
                    }

                    exportRows.Add(new ExcelExportRowDto
                    {
                        PoNumber = otm.PoNumber ?? "-",
                        ProductName = otm.Remarks ?? "-",
                        Department = !string.IsNullOrEmpty(latestPlan?.Department) ? latestPlan.Department : (!string.IsNullOrEmpty(otm.Urgency) ? otm.Urgency : "-"),
                        Urgency = !string.IsNullOrEmpty(otm.Urgency) ? otm.Urgency : "-",
                        DeliveryTarget = !string.IsNullOrEmpty(latestPlan?.DeliveryTarget) ? latestPlan.DeliveryTarget : "-",
                        Source = sourceText,
                        ActionType = actionType,
                        ActionLabel = actionLabel,
                        Status = statusText,
                        Quantity = !string.IsNullOrEmpty(otm.RemarksQuantity) ? otm.RemarksQuantity : "1",
                        Amount = amount,
                        DateText = otm.ApprovedDate ?? "-"
                    });
                }
            }

            // C. กรองตาม actionFilter (ถ้าไม่ใช่ "all" และไม่ใช่ "DraftOnly")
            if (!string.IsNullOrEmpty(actionFilter) && actionFilter != "all" && actionFilter != "DraftOnly")
            {
                exportRows = exportRows.Where(r => r.ActionType.Equals(actionFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // 5. สร้าง Workbook & Format ตารางให้อ่านเข้าใจง่าย ไม่มีสีจัดจ้าน
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var ws = workbook.Worksheets.Add("รายงานค่าใช้จ่ายประจำเดือน");

            var fontName = "Noto Sans Thai";
            var colorBorder = ClosedXML.Excel.XLColor.FromHtml("#CBD5E1");

            // Title
            ws.Cell(1, 1).Value = $"รายงานสรุปรายการสั่งผลิตและค่าใช้จ่าย — {canonicalThaiMonth}";
            ws.Cell(1, 1).Style.Font.FontName = fontName;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.Black;
            ws.Range(1, 1, 1, 12).Merge();

            // เงื่อนไข Filter ภาษาไทยสำหรับ Subtitle
            string actionTextDesc = actionFilter switch
            {
                "ReceivedFull" => "เฉพาะรับสินค้าแล้ว",
                "Deferred" => "เฉพาะผ่อนชำระ",
                "Skipped" => "เฉพาะยังไม่รับสินค้า",
                "DraftOnly" => "เฉพาะรายการจำลองในตะกร้า",
                _ => "ทั้งหมดทุกประเภท"
            };
            string statusTextDesc = statusFilter switch
            {
                "saved" => "เฉพาะที่บันทึกแล้ว",
                "pending" => "เฉพาะที่รอดำเนินการ",
                _ => "ทั้งหมด (บันทึกแล้ว + รอดำเนินการ)"
            };

            // Sub-info
            ws.Cell(2, 1).Value = $"เงื่อนไข: หมวดหมู่ [{actionTextDesc}] | สถานะ [{statusTextDesc}] | รวมทั้งสิ้น: {exportRows.Count} รายการ | วันที่ออกรายงาน: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell(2, 1).Style.Font.FontName = fontName;
            ws.Cell(2, 1).Style.Font.FontSize = 10;
            ws.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#475569");
            ws.Range(2, 1, 2, 12).Merge();

            ws.Row(3).Height = 8;

            // Header row (White background, No fill color, Clear borders)
            int headerRow = 4;
            var headers = new[]
            {
                "ลำดับ", "เลขที่อนุมัติ (PO)", "ชื่อสินค้า / รายการอะไหล่", "แผนก / หน่วยงาน", "ความเร่งด่วน",
                "เป้าหมายส่งมอบ", "ที่มาของรายการ", "การดำเนินการ", "สถานะ", "จำนวน", "มูลค่า (บาท)", "วันที่บันทึก/อนุมัติ"
            };

            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.FontName = fontName;
                cell.Style.Font.FontSize = 11;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.Black;
                cell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = colorBorder;
            }

            ws.Row(headerRow).Height = 24;

            int currentRow = headerRow + 1;
            int itemNo = 1;
            decimal totalAmount = 0m;
            decimal receivedAmount = 0m;
            decimal deferredAmount = 0m;
            decimal skippedOrPendingAmount = 0m;

            foreach (var row in exportRows)
            {
                totalAmount += row.Amount;
                if (row.ActionType == "ReceivedFull") receivedAmount += row.Amount;
                else if (row.ActionType == "Deferred") deferredAmount += row.Amount;
                else skippedOrPendingAmount += row.Amount;

                ws.Cell(currentRow, 1).Value = itemNo++;
                ws.Cell(currentRow, 2).Value = row.PoNumber;
                ws.Cell(currentRow, 3).Value = row.ProductName;
                ws.Cell(currentRow, 4).Value = row.Department;
                ws.Cell(currentRow, 5).Value = row.Urgency;
                ws.Cell(currentRow, 6).Value = row.DeliveryTarget;
                ws.Cell(currentRow, 7).Value = row.Source;
                ws.Cell(currentRow, 8).Value = row.ActionLabel;
                ws.Cell(currentRow, 9).Value = row.Status;
                ws.Cell(currentRow, 10).Value = row.Quantity;
                ws.Cell(currentRow, 11).Value = row.Amount;
                ws.Cell(currentRow, 12).Value = row.DateText;

                for (int c = 1; c <= 12; c++)
                {
                    var cell = ws.Cell(currentRow, c);
                    cell.Style.Font.FontName = fontName;
                    cell.Style.Font.FontSize = 10;
                    cell.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = colorBorder;
                    cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                }

                ws.Cell(currentRow, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 2).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 3).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 3).Style.Alignment.WrapText = true;
                ws.Cell(currentRow, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 11).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 12).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Row(currentRow).Height = 22;
                currentRow++;
            }

            // Summary Rows (Clean bottom summary)
            currentRow++;
            if (receivedAmount > 0)
            {
                ws.Cell(currentRow, 9).Value = "รวมยอดรับสินค้าแล้ว";
                ws.Cell(currentRow, 9).Style.Font.FontName = fontName;
                ws.Cell(currentRow, 9).Style.Font.Bold = true;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 11).Value = receivedAmount;
                ws.Cell(currentRow, 11).Style.Font.FontName = fontName;
                ws.Cell(currentRow, 11).Style.Font.Bold = true;
                ws.Cell(currentRow, 11).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                currentRow++;
            }

            if (deferredAmount > 0)
            {
                ws.Cell(currentRow, 9).Value = "รวมยอดผ่อนชำระ";
                ws.Cell(currentRow, 9).Style.Font.FontName = fontName;
                ws.Cell(currentRow, 9).Style.Font.Bold = true;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 11).Value = deferredAmount;
                ws.Cell(currentRow, 11).Style.Font.FontName = fontName;
                ws.Cell(currentRow, 11).Style.Font.Bold = true;
                ws.Cell(currentRow, 11).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                currentRow++;
            }

            if (skippedOrPendingAmount > 0)
            {
                ws.Cell(currentRow, 9).Value = "รวมยอดยังไม่รับ/รอดำเนินการ";
                ws.Cell(currentRow, 9).Style.Font.FontName = fontName;
                ws.Cell(currentRow, 9).Style.Font.Bold = true;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 11).Value = skippedOrPendingAmount;
                ws.Cell(currentRow, 11).Style.Font.FontName = fontName;
                ws.Cell(currentRow, 11).Style.Font.Bold = true;
                ws.Cell(currentRow, 11).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                currentRow++;
            }

            // Grand Total Row (With double bottom border)
            ws.Cell(currentRow, 9).Value = $"รวมมูลค่าทั้งสิ้น ({exportRows.Count} รายการ)";
            ws.Cell(currentRow, 9).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 9).Style.Font.Bold = true;
            ws.Cell(currentRow, 9).Style.Font.FontSize = 11;
            ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            ws.Cell(currentRow, 11).Value = totalAmount;
            ws.Cell(currentRow, 11).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 11).Style.Font.Bold = true;
            ws.Cell(currentRow, 11).Style.Font.FontSize = 11;
            ws.Cell(currentRow, 11).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;

            ws.Range(currentRow, 9, currentRow, 11).Style.Border.TopBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
            ws.Range(currentRow, 9, currentRow, 11).Style.Border.TopBorderColor = colorBorder;
            ws.Range(currentRow, 9, currentRow, 11).Style.Border.BottomBorder = ClosedXML.Excel.XLBorderStyleValues.Double;
            ws.Range(currentRow, 9, currentRow, 11).Style.Border.BottomBorderColor = ClosedXML.Excel.XLColor.Black;

            // ── Column Widths (Well-proportioned, Easy to Read) ─────────────
            ws.Column(1).Width = 8;   // ลำดับ
            ws.Column(2).Width = 20;  // เลขที่อนุมัติ (PO)
            ws.Column(3).Width = 45;  // ชื่อสินค้า / รายการอะไหล่
            ws.Column(4).Width = 18;  // แผนก / หน่วยงาน
            ws.Column(5).Width = 16;  // ความเร่งด่วน
            ws.Column(6).Width = 18;  // เป้าหมายส่งมอบ
            ws.Column(7).Width = 28;  // ที่มาของรายการ
            ws.Column(8).Width = 22;  // การดำเนินการ
            ws.Column(9).Width = 16;  // สถานะ
            ws.Column(10).Width = 10; // จำนวน
            ws.Column(11).Width = 22; // มูลค่ารวม (บาท)
            ws.Column(12).Width = 20; // วันที่บันทึก/อนุมัติ

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            string cleanMonth = canonicalThaiMonth.Trim().Replace(" ", "_");
            string downloadName = $"รายงานค่าใช้จ่าย_{cleanMonth}_{actionFilter}.xlsx";
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                downloadName);
        }

        // GET: /MonthlyCost/ExportYearlySummaryExcel?year=2026&month=7&status=ReceivedFull&search=...&customName=...
        [HttpGet]
        public async Task<IActionResult> ExportYearlySummaryExcel(int? year, int? month, string? status, string? search, string? customName)
        {
            var now = _dateTimeProvider.Now;
            var selectedYear = year ?? now.Year;

            var thaiMonths = new[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            var yearPrefix = $"{selectedYear:0000}-";

            var allActions = await _context.MonthlyOrderActions
                .Include(a => a.OrderTrackingMaster)
                .ThenInclude(o => o!.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .Where(a => a.MonthYear.StartsWith(yearPrefix))
                .ToListAsync();

            var deduplicated = allActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .Select(g => g.OrderByDescending(a => a.CreatedAt).First())
                .ToList();

            var items = new List<YearlyOrderItem>();

            foreach (var act in deduplicated)
            {
                var otm = act.OrderTrackingMaster;
                if (otm == null) continue;

                var latestPlan = otm.MatchedInWeeklyPlans
                    .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                    .FirstOrDefault();

                string orderName = !string.IsNullOrWhiteSpace(otm.Remarks)
                    ? otm.Remarks.Replace("สั่งทำ ", "").Replace("สั่งทำ", "").Trim()
                    : "ไม่ระบุ";

                string dept = latestPlan?.Department ?? otm.Urgency ?? "-";

                decimal amount = 0m;
                if (act.Action == "Deferred" || act.Action == "ReceivedFull")
                {
                    amount = act.ActionPrice > 0 ? act.ActionPrice : 0m;
                }
                if (amount == 0 && !string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var parsed))
                {
                    amount = parsed;
                }

                string actionDisplay = act.Action switch
                {
                    "ReceivedFull" => "รับสินค้าแล้ว",
                    "Deferred" => "ผ่อนชำระ",
                    "Skipped" => "ยังไม่รับสินค้า",
                    _ => act.Action
                };

                var normalizedKey = act.MonthYear.Length > 7 ? act.MonthYear.Substring(0, 7) : act.MonthYear;
                var keyParts = normalizedKey.Split('-');
                int monthNum = keyParts.Length == 2 && int.TryParse(keyParts[1], out var mn) ? mn : 0;
                int yearNum = keyParts.Length >= 1 && int.TryParse(keyParts[0], out var yn) ? yn : selectedYear;
                string monthDisplay = monthNum >= 1 && monthNum <= 12
                    ? $"{thaiMonths[monthNum]} {yearNum + 543}"
                    : normalizedKey;

                string qtyDisplay = !string.IsNullOrWhiteSpace(otm.RemarksQuantity)
                    ? (otm.RemarksQuantity.Trim().EndsWith("ชิ้น") ? otm.RemarksQuantity.Trim() : $"{otm.RemarksQuantity.Trim()} ชิ้น")
                    : "-";

                var approvedDt = ParseThaiDate(otm.ApprovedDate);
                string approvedMonthDisplay = "-";
                int approvedMonthNum = 0;
                if (approvedDt.HasValue)
                {
                    approvedMonthNum = approvedDt.Value.Month;
                    approvedMonthDisplay = approvedMonthNum >= 1 && approvedMonthNum <= 12
                        ? $"{thaiMonths[approvedMonthNum]} {approvedDt.Value.Year + 543}"
                        : "-";
                }

                items.Add(new YearlyOrderItem
                {
                    ActionId = act.Id,
                    OrderId = otm.Id,
                    PoNumber = otm.PoNumber ?? "-",
                    OrderName = orderName,
                    Quantity = qtyDisplay,
                    Department = dept,
                    Amount = amount,
                    MonthKey = normalizedKey,
                    MonthDisplay = monthDisplay,
                    MonthNumber = monthNum,
                    Action = act.Action,
                    ActionDisplay = actionDisplay,
                    ApprovedDate = otm.ApprovedDate ?? "-",
                    ApprovedMonthDisplay = approvedMonthDisplay,
                    ApprovedMonthNumber = approvedMonthNum,
                    CreatedAt = act.CreatedAt
                });
            }

            // Apply Filters
            if (month.HasValue && month.Value >= 1 && month.Value <= 12)
            {
                items = items.Where(i => i.ApprovedMonthNumber == month.Value).ToList();
            }

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(i => i.Action.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var keywords = search.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (keywords.Length > 0)
                {
                    items = items.Where(i => keywords.Any(kw =>
                        i.PoNumber.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        i.OrderName.Contains(kw, StringComparison.OrdinalIgnoreCase))).ToList();
                }
            }

            // Order items logically (by Approved Month, then PO)
            items = items.OrderBy(i => i.ApprovedMonthNumber).ThenBy(i => i.PoNumber).ToList();

            // ClosedXML
            ClosedXML.Excel.LoadOptions.DefaultGraphicEngine = new MockGraphicEngine();
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var ws = workbook.Worksheets.Add($"สรุปรายการสั่งซื้อ {selectedYear + 543}");

            var fontName = "Noto Sans Thai";
            var colorBorder = ClosedXML.Excel.XLColor.FromHtml("#E2E8F0");

            // Title
            ws.Cell(1, 1).Value = $"รายงานสรุปรายการสั่งซื้อประจำปี พ.ศ. {selectedYear + 543}";
            ws.Cell(1, 1).Style.Font.FontName = fontName;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#1E293B");
            ws.Range(1, 1, 1, 6).Merge();

            // Filter subtitle
            string monthFilterText = month.HasValue && month.Value >= 1 && month.Value <= 12 ? thaiMonths[month.Value] : "ทุกเดือน";
            string statusFilterText = status switch
            {
                "ReceivedFull" => "รับสินค้าแล้ว",
                "Deferred" => "ผ่อนชำระ",
                "Skipped" => "ยังไม่รับสินค้า",
                _ => "ทุกสถานะ"
            };
            ws.Cell(2, 1).Value = $"ปี: พ.ศ. {selectedYear + 543} | เดือนตามใบสั่งผลิต: {monthFilterText} | สถานะ: {statusFilterText} | ข้อมูลที่ส่งออก: {items.Count} รายการ | วันที่ออกรายงาน: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell(2, 1).Style.Font.FontName = fontName;
            ws.Cell(2, 1).Style.Font.FontSize = 9.5;
            ws.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#64748B");
            ws.Range(2, 1, 2, 6).Merge();

            ws.Row(3).Height = 8;

            int headerRow = 4;
            var headers = new[]
            {
                "เลขที่อนุมัติ (PO)", "ชื่อรายการ / สินค้า", "จำนวน", "มูลค่า (บาท)", "เดือนตามใบสั่งผลิต", "สถานะ"
            };

            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.FontName = fontName;
                cell.Style.Font.FontSize = 10.5;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#334155");
                cell.Style.Alignment.Horizontal = c == 1 ? ClosedXML.Excel.XLAlignmentHorizontalValues.Left :
                                                  c == 3 ? ClosedXML.Excel.XLAlignmentHorizontalValues.Right :
                                                  ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = colorBorder;
                cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#F8FAFC");
            }
            ws.Row(headerRow).Height = 26;

            int currentRow = headerRow + 1;
            decimal totalAmt = 0m;

            foreach (var item in items)
            {
                totalAmt += item.Amount;

                ws.Cell(currentRow, 1).Value = item.PoNumber;
                ws.Cell(currentRow, 2).Value = item.OrderName;
                ws.Cell(currentRow, 3).Value = item.Quantity;
                ws.Cell(currentRow, 4).Value = item.Amount;
                ws.Cell(currentRow, 5).Value = item.ApprovedMonthDisplay;
                ws.Cell(currentRow, 6).Value = item.ActionDisplay;

                for (int c = 1; c <= 6; c++)
                {
                    var cell = ws.Cell(currentRow, c);
                    cell.Style.Font.FontName = fontName;
                    cell.Style.Font.FontSize = 10;
                    cell.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = colorBorder;
                    cell.Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;
                }

                ws.Cell(currentRow, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 2).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;
                ws.Cell(currentRow, 3).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                
                // Status Cell Styling (Highlight only specific statuses)
                var statusCell = ws.Cell(currentRow, 6);
                statusCell.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                statusCell.Style.Font.Bold = true;

                if (item.Action == "ReceivedFull")
                {
                    statusCell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#D1FAE5"); // Light green
                    statusCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#065F46"); // Dark green
                }
                else if (item.Action == "Deferred")
                {
                    statusCell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#FEF3C7"); // Light amber
                    statusCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#92400E"); // Dark amber
                }
                else
                {
                    statusCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#64748B"); // Slate
                }

                ws.Row(currentRow).Height = 22;
                currentRow++;
            }

            // Summary Row at the bottom
            if (items.Count > 0)
            {
                var summaryRow = currentRow;
                ws.Cell(summaryRow, 1).Value = $"รวมทั้งสิ้น ({items.Count} รายการ)";
                ws.Range(summaryRow, 1, summaryRow, 3).Merge();
                ws.Cell(summaryRow, 1).Style.Font.FontName = fontName;
                ws.Cell(summaryRow, 1).Style.Font.Bold = true;
                ws.Cell(summaryRow, 1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(summaryRow, 1).Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;

                ws.Cell(summaryRow, 4).Value = totalAmt;
                ws.Cell(summaryRow, 4).Style.Font.FontName = fontName;
                ws.Cell(summaryRow, 4).Style.Font.Bold = true;
                ws.Cell(summaryRow, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(summaryRow, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(summaryRow, 4).Style.Alignment.Vertical = ClosedXML.Excel.XLAlignmentVerticalValues.Center;

                for (int c = 1; c <= 6; c++)
                {
                    var cell = ws.Cell(summaryRow, c);
                    cell.Style.Font.FontName = fontName;
                    cell.Style.Border.OutsideBorder = ClosedXML.Excel.XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = colorBorder;
                    cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#F8FAFC");
                }
                ws.Row(summaryRow).Height = 24;
            }

            ws.Column(1).Width = 20; // เลขที่อนุมัติ (PO)
            ws.Column(2).Width = 45; // ชื่อรายการ
            ws.Column(3).Width = 14; // จำนวน
            ws.Column(4).Width = 22; // มูลค่า (บาท)
            ws.Column(5).Width = 22; // เดือนตามใบสั่งผลิต
            ws.Column(6).Width = 20; // สถานะ

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            string finalFileName = !string.IsNullOrWhiteSpace(customName)
                ? (customName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? customName : $"{customName}.xlsx")
                : $"รายงานสรุปรายการสั่งซื้อ_{selectedYear + 543}_{(month.HasValue ? thaiMonths[month.Value] : "ทั้งปี")}_{status ?? "ทั้งหมด"}.xlsx";

            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                finalFileName);
        }

        private class ExcelExportRowDto
        {
            public string PoNumber { get; set; } = "-";
            public string ProductName { get; set; } = "-";
            public string Department { get; set; } = "-";
            public string Urgency { get; set; } = "-";
            public string DeliveryTarget { get; set; } = "-";
            public string Source { get; set; } = "-";
            public string ActionType { get; set; } = "-";
            public string ActionLabel { get; set; } = "-";
            public string Status { get; set; } = "-";
            public string Quantity { get; set; } = "1";
            public decimal Amount { get; set; }
            public string DateText { get; set; } = "-";
        }

        // ─── Time Travel / Date Mocking Endpoints ─────────────────────────────────────
        [HttpPost]
        [HttpGet]
        public async Task<IActionResult> SetMockDate([FromBody] SetMockDateRequest? model, [FromQuery] string? dateStr)
        {
            var date = model?.DateStr ?? dateStr;
            if (string.IsNullOrWhiteSpace(date) || !DateTime.TryParse(date, out var parsedDate))
            {
                return Json(new { success = false, message = "รูปแบบวันที่ไม่ถูกต้อง" });
            }

            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║ ⏳ [DEV TIME TRAVEL] ได้รับคำขอกดข้ามเดือน / ปรับเวลาจำลอง          ║");
            Console.WriteLine($"║ ⏰ เวลาที่กดคำขอ: {DateTime.Now:dd/MM/yyyy HH:mm:ss}                              ║");
            Console.WriteLine($"║ 🎯 เวลาจำลองใหม่: {parsedDate:dd/MM/yyyy} (งวดเดือน: {parsedDate:yyyy-MM})                  ║");
            Console.WriteLine($"║ 📊 การลง Google Sheets: ❌ [ไม่ส่งชีท] (รอดำเนินการเฉพาะใน DB ภายใน) ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");

            Response.Cookies.Append("MockSystemDate", parsedDate.ToString("yyyy-MM-dd"), new CookieOptions
            {
                Expires = DateTimeOffset.Now.AddDays(7),
                HttpOnly = true,
                IsEssential = true
            });

            _mockDateStore.MockDate = parsedDate;

            // ตรวจสอบและ auto-skip ทันทีที่มีการเลื่อนเวลา โดยส่ง parsedDate เข้าไปตรงๆ
            await AutoClosePriorMonthsAsync(parsedDate);

            return Json(new { success = true, mockedDate = parsedDate.ToString("yyyy-MM-dd") });
        }

        [HttpPost]
        [HttpGet]
        public IActionResult ResetMockDate()
        {
            Console.WriteLine("\n╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║ ⏳ [DEV TIME TRAVEL] กดปุ่ม 'คืนค่าเวลาจริง'                      ║");
            Console.WriteLine($"║ ⏰ เวลาจริงปัจจุบัน: {DateTime.Now:dd/MM/yyyy HH:mm:ss}                          ║");
            Console.WriteLine($"║ 📊 การลง Google Sheets: ❌ [ไม่ส่งชีท]                           ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝\n");

            Response.Cookies.Delete("MockSystemDate");
            _mockDateStore.MockDate = null;
            return Json(new { success = true });
        }

        // ─── Trigger Month-End Transition (Auto-Skip un-actioned & Sync to Sheet) ───
        [HttpPost]
        public async Task<IActionResult> TriggerMonthEndTransition([FromBody] TriggerMonthEndTransitionRequest? request)
        {
            try
            {
                // 1. ระบุเดือนต้นทาง (fromMonthKey) ที่กำลังจะปิดงวด
                DateTime currentDate = _dateTimeProvider.Now;
                string fromMonthKey = currentDate.ToString("yyyy-MM");

                if (!string.IsNullOrWhiteSpace(request?.FromMonthYear))
                {
                    if (request.FromMonthYear.Contains(" "))
                    {
                        fromMonthKey = ConvertThaiMonthToKey(request.FromMonthYear);
                    }
                    else if (request.FromMonthYear.Contains("-"))
                    {
                        fromMonthKey = request.FromMonthYear.Length > 7 ? request.FromMonthYear.Substring(0, 7) : request.FromMonthYear;
                    }
                }

                // คำนวณเดือนถัดไป (toMonthKey)
                var parts = fromMonthKey.Split('-');
                int fromYear = int.Parse(parts[0]);
                int fromMonth = int.Parse(parts[1]);
                var nextDate = new DateTime(fromYear, fromMonth, 1).AddMonths(1);
                string toMonthKey = $"{nextDate.Year:0000}-{nextDate.Month:02}";

                Console.WriteLine("\n╔══════════════════════════════════════════════════════════════════╗");
                Console.WriteLine($"║ 🏁 [MONTH-END TRANSITION] เริ่มต้นการตัดรอบสิ้นเดือนอัตโนมัติ       ║");
                Console.WriteLine($"║ ⏰ เวลาดำเนินการ: {DateTime.Now:dd/MM/yyyy HH:mm:ss}                               ║");
                Console.WriteLine($"║ 📤 ปิดงวดเดือน: {fromMonthKey} ({ConvertKeyToThaiMonth(fromMonthKey)})                     ║");
                Console.WriteLine($"║ 📥 ยกยอดเข้าสู่เดือน: {toMonthKey} ({ConvertKeyToThaiMonth(toMonthKey)})                   ║");
                Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");

                // 2. ดึงรายการ Orders ทั้งหมดที่อนุมัติก่อนหรือในเดือนนี้
                var allOrders = await _context.OrderTrackingMasters
                    .Include(o => o.MatchedInWeeklyPlans)
                    .ThenInclude(m => m.WeeklyPlan)
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();

                var priorActions = await _context.MonthlyOrderActions.ToListAsync();

                var actionByOrderAndMonth = priorActions
                    .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear.Length > 7 ? a.MonthYear.Substring(0, 7) : a.MonthYear))
                    .ToDictionary(g => g.Key, g => g.First());

                var receivedFullOrders = priorActions
                    .Where(a => a.Action == "ReceivedFull")
                    .Select(a => a.OrderTrackingMasterId)
                    .ToHashSet();

                int skippedAdded = 0;
                DateTime fromMonthStart = new DateTime(fromYear, fromMonth, 1);

                foreach (var order in allOrders)
                {
                    if (receivedFullOrders.Contains(order.Id)) continue;

                    var approvedDt = ParseThaiDate(order.ApprovedDate);
                    if (!approvedDt.HasValue) continue;

                    DateTime approvedStart = new DateTime(approvedDt.Value.Year, approvedDt.Value.Month, 1);
                    if (approvedStart > fromMonthStart) continue; // ออเดอร์ในอนาคตยังไม่ถึงรอบ

                    // ถ้าใน fromMonthKey ยังไม่มี Action ใดๆ ให้ Auto-Skip ทันที
                    if (!actionByOrderAndMonth.ContainsKey((order.Id, fromMonthKey)))
                    {
                        var autoSkip = new MonthlyOrderAction
                        {
                            Id = Guid.NewGuid(),
                            OrderTrackingMasterId = order.Id,
                            MonthYear = fromMonthKey,
                            Action = "Skipped",
                            ActionPrice = 0,
                            IsForcedPayment = false,
                            DeferredFromMonth = null,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };

                        _context.MonthlyOrderActions.Add(autoSkip);
                        actionByOrderAndMonth[(order.Id, fromMonthKey)] = autoSkip;
                        skippedAdded++;
                    }
                }

                await _context.SaveChangesAsync();
                Console.WriteLine($"📝 [MONTH-END TRANSITION] Auto-Skip รายการที่ค้างในเดือน {fromMonthKey} จำนวน {skippedAdded} รายการ");

                // 3. ส่งข้อมูลเต็มชุดของเดือนเดิม (fromMonthKey) ไปยัง Google Sheets
                try
                {
                    await _syncService.SyncSpecificMonthsToGoogleSheetsAsync(
                        new List<string> { fromMonthKey },
                        reason: $"ตัดรอบสิ้นเดือนอัตโนมัติ: ปิดยอดคงเหลือเดือน {fromMonthKey} ({skippedAdded} รายการผลัด) และอัปเดตชีทให้ครบถ้วน");
                }
                catch (Exception syncEx)
                {
                    Console.WriteLine($"❌ [MONTH-END TRANSITION] Google Sheets Sync Error: {syncEx.Message}");
                }

                // 4. ข้ามเวลาไปยังวันที่ 1 ของเดือนถัดไป (toMonthKey)
                Response.Cookies.Append("MockSystemDate", nextDate.ToString("yyyy-MM-dd"), new CookieOptions
                {
                    Expires = DateTimeOffset.Now.AddDays(7),
                    HttpOnly = true,
                    IsEssential = true
                });

                _mockDateStore.MockDate = nextDate;

                await _hubContext.Clients.All.SendAsync("ReceiveMonthlyCostUpdate");

                string nextThaiMonth = ConvertKeyToThaiMonth(toMonthKey);

                return Json(new
                {
                    success = true,
                    fromMonth = fromMonthKey,
                    toMonth = toMonthKey,
                    nextMonthThai = nextThaiMonth,
                    nextDate = nextDate.ToString("yyyy-MM-dd"),
                    skippedCount = skippedAdded,
                    message = $"ตัดรอบเดือน {ConvertKeyToThaiMonth(fromMonthKey)} เรียบร้อยแล้ว (ผลัด {skippedAdded} รายการ) และเข้าสู่งวด {nextThaiMonth}"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in TriggerMonthEndTransition: {ex.Message}");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        private async Task AutoClosePriorMonthsAsync(DateTime? overrideNow = null)
        {
            try
            {
                // ทำความสะอาด MonthYear ที่ผิดปกติใน DB อัตโนมัติ (เช่น 2025-122 -> 2025-12)
                try
                {
                    await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET MonthYear = SUBSTR(MonthYear, 1, 7) WHERE LENGTH(MonthYear) > 7;");
                    await _context.Database.ExecuteSqlRawAsync("UPDATE MonthlyOrderActions SET DeferredFromMonth = SUBSTR(DeferredFromMonth, 1, 7) WHERE DeferredFromMonth IS NOT NULL AND LENGTH(DeferredFromMonth) > 7;");
                }
                catch { }

                var now = overrideNow ?? _dateTimeProvider.Now;
                var currentMonthKey = now.ToString("yyyy-MM");

                var allOrders = await _context.OrderTrackingMasters
                    .Include(o => o.MatchedInWeeklyPlans)
                    .ThenInclude(m => m.WeeklyPlan)
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();

                var priorActions = await _context.MonthlyOrderActions.ToListAsync();

                var actionByOrderAndMonth = priorActions
                    .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear.Length > 7 ? a.MonthYear.Substring(0, 7) : a.MonthYear))
                    .ToDictionary(g => g.Key, g => g.First());

                var receivedFullOrders = priorActions
                    .Where(a => a.Action == "ReceivedFull")
                    .Select(a => a.OrderTrackingMasterId)
                    .ToHashSet();

                bool hasNewActions = false;
                var affectedMonths = new HashSet<string>();

                foreach (var order in allOrders)
                {
                    if (receivedFullOrders.Contains(order.Id)) continue;

                    var approvedDt = ParseThaiDate(order.ApprovedDate);
                    if (!approvedDt.HasValue) continue;

                    string approvedMonthKey = $"{approvedDt.Value.Year:0000}-{approvedDt.Value.Month:02}";

                    DateTime loopDate = new DateTime(approvedDt.Value.Year, approvedDt.Value.Month, 1);
                    DateTime currentDate = new DateTime(now.Year, now.Month, 1);

                    // วนลูปสร้าง Skipped ให้ทุกเดือนที่ขาดหายไป จนกว่าจะถึงเดือนก่อนหน้าเดือนปัจจุบัน
                    while (loopDate < currentDate)
                    {
                        string loopMonthKey = $"{loopDate.Year:0000}-{loopDate.Month:02}";

                        if (!actionByOrderAndMonth.ContainsKey((order.Id, loopMonthKey)))
                        {
                            var autoSkip = new MonthlyOrderAction
                            {
                                Id = Guid.NewGuid(),
                                OrderTrackingMasterId = order.Id,
                                MonthYear = loopMonthKey,
                                Action = "Skipped",
                                ActionPrice = 0,
                                IsForcedPayment = false,
                                DeferredFromMonth = null,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            _context.MonthlyOrderActions.Add(autoSkip);
                            actionByOrderAndMonth[(order.Id, loopMonthKey)] = autoSkip;
                            affectedMonths.Add(loopMonthKey);
                            hasNewActions = true;
                        }
                        loopDate = loopDate.AddMonths(1);
                    }
                }

                if (hasNewActions)
                {
                    await _context.SaveChangesAsync();
                    Console.WriteLine($"\n┌──────────────────────────────────────────────────────────────────");
                    Console.WriteLine($"│ 📝 [AUTO-SKIP DB] ประมวลผลสร้างสถานะ 'ยังไม่รับสินค้า' ลงใน SQLite สำเร็จ");
                    Console.WriteLine($"│ 📅 จำนวน {affectedMonths.Count} เดือนในอดีต: [{string.Join(", ", affectedMonths)}]");
                    Console.WriteLine($"│ 🚫 [GOOGLE SHEETS] ❌ ไม่ได้ส่งไป Google Sheets (เนื่องจากตั้งค่าให้ส่งเฉพาะเมื่อผู้ใช้กดปุ่ม 'บันทึก' เองเท่านั้น)");
                    Console.WriteLine($"└──────────────────────────────────────────────────────────────────\n");
                }
                else
                {
                    Console.WriteLine($"[AutoClosePriorMonthsAsync] ℹ️ ไม่มีรายการค้างที่ต้อง Auto-skip (Google Sheets: ❌ ไม่ได้ส่ง)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in AutoClosePriorMonthsAsync: {ex.Message}");
            }
        }
    }

    public class RevertActionRequest
    {
        public Guid ActionId { get; set; }
    }

    public class BulkRevertActionRequest
    {
        public List<Guid> ActionIds { get; set; } = new();
    }

    public class SaveActionsRequest
    {
        public string MonthYear { get; set; } = string.Empty;
        public List<Guid> AllPendingOrderIds { get; set; } = new(); // list ของ pending orders ทั้งหมด
        public List<OrderAction> Actions { get; set; } = new();
        public bool IsCloseMonth { get; set; } = false; // แฟล็กระบุว่าเป็นการปิดยอดเดือนหรือไม่
    }

    public class OrderAction
    {
        public Guid OrderId { get; set; }
        public string Action { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    public class SetMockDateRequest
    {
        public string DateStr { get; set; } = string.Empty;
    }

    public class TriggerMonthEndTransitionRequest
    {
        public string? FromMonthYear { get; set; }
    }

    public class DraftCartItem
    {
        public string action { get; set; } = string.Empty;
        public decimal actionPrice { get; set; }
    }
}
