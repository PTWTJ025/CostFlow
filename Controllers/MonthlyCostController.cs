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

        public MonthlyCostController(AppDbContext context, IDateTimeProvider dateTimeProvider,
            IHubContext<DashboardHub> hubContext, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _dateTimeProvider = dateTimeProvider;
            _hubContext = hubContext;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
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

            // Parse month/year from Thai format (e.g., "กรกฎาคม 2569")
            var monthYearKey = ConvertThaiMonthToKey(id);

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

            var viewModel = new MonthlyCostDetailViewModel
            {
                MonthYear = id,
                PendingOrders = pendingItems,
                SavedOrders = savedItems,
                Stats = stats,
                IsCurrentMonth = (monthYearKey == currentMonthKey),
                IsFutureMonth = string.Compare(monthYearKey, currentMonthKey) > 0
            };

            ViewData["HeaderTitle"] = $"จัดการค่าใช้จ่าย - {id}";
            return View(viewModel);
        }

        private string ConvertThaiMonthToKey(string thaiMonthYear)
        {
            if (string.IsNullOrWhiteSpace(thaiMonthYear)) return _dateTimeProvider.Now.ToString("yyyy-MM");
            var parts = thaiMonthYear.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return _dateTimeProvider.Now.ToString("yyyy-MM");

            var thaiMonths = new Dictionary<string, int>
            {
                { "มกราคม", 1 }, { "กุมภาพันธ์", 2 }, { "มีนาคม", 3 }, { "เมษายน", 4 },
                { "พฤษภาคม", 5 }, { "มิถุนายน", 6 }, { "กรกฎาคม", 7 }, { "สิงหาคม", 8 },
                { "กันยายน", 9 }, { "ตุลาคม", 10 }, { "พฤศจิกายน", 11 }, { "ธันวาคม", 12 }
            };

            if (int.TryParse(parts[1], out var buddhistYear) && thaiMonths.TryGetValue(parts[0], out var month))
            {
                var year = buddhistYear - 543; // Convert Buddhist year to Gregorian
                return $"{year:0000}-{month:00}";
            }

            return _dateTimeProvider.Now.ToString("yyyy-MM");
        }

        private string ConvertKeyToThaiMonth(string key, bool shortYear = false)
        {
            if (string.IsNullOrEmpty(key) || !key.Contains("-")) return key;
            var parts = key.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var year) ||
                !int.TryParse(parts[1], out var month)) return key;

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
                        DeferredFromMonth = action.Action == "Deferred" ? monthYearKey : null,
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
                // ส่งข้อมูลไปสำรองใน Google Sheets (ใช้โครงสร้างเดียวกับ Apps Script เดิม)
                // ==========================================
                try
                {
                    string? appScriptUrl = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
                    if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
                    {
                        appScriptUrl = _configuration["GoogleSheets:ArchiveAppScriptUrl"];
                    }

                    if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                    {
                        var allOrderIdsToSync = request.IsCloseMonth
                            ? request.Actions.Select(a => a.OrderId).Concat(unselectedIds).Distinct().ToList()
                            : request.Actions.Select(a => a.OrderId).Distinct().ToList();
                        var orderDetailsMap = await _context.OrderTrackingMasters
                            .Include(o => o.Report)
                            .Include(o => o.MatchedInWeeklyPlans)
                            .ThenInclude(m => m.WeeklyPlan)
                            .Where(o => allOrderIdsToSync.Contains(o.Id))
                            .ToDictionaryAsync(o => o.Id, o => o);

                        var timestampNow = DateTime.Now.ToString("dd/MM/yyyy HH:mm",
                            new System.Globalization.CultureInfo("th-TH"));

                        var formattedActionItems = new List<(string MonthYear, object?[] Row)>();

                        var selectedActionMap = request.Actions.ToDictionary(a => a.OrderId, a => a);

                        foreach (var orderId in allOrderIdsToSync)
                        {
                            if (!orderDetailsMap.TryGetValue(orderId, out var otm)) continue;

                            var latestPlan = otm.MatchedInWeeklyPlans
                                .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                                .FirstOrDefault();

                            // จัดกลุ่มการลงบันทึกในงวดบัญชีขณะนี้ (เช่น มกราคม 2569)
                            string orderMonthKey = monthYearKey;

                            latestPriorActionMap.TryGetValue(orderId, out var priorAction);
                            string deferredFrom =
                                priorAction != null ? ConvertKeyToThaiMonth(priorAction.MonthYear) : "-";

                            string poNo = otm.PoNumber ?? "-";
                            string orderName = !string.IsNullOrWhiteSpace(otm.Remarks) ? otm.Remarks.Trim() : "ไม่ระบุ";
                            string dept = latestPlan?.Department ?? otm.Urgency ?? "-";
                            string deliveryTarget = latestPlan?.DeliveryTarget ?? "-";
                            string requestDateDisplay = otm.RequestDate ?? "-";
                            string approvedDateDisplay = otm.ApprovedDate ?? "-";

                            selectedActionMap.TryGetValue(orderId, out var actReq);
                            string actionStatus = actReq?.Action ?? "Skipped";
                            decimal paidAmount = actReq != null ? actReq.Price : 0m;

                            decimal itemTotalPrice = 0m;
                            if (!string.IsNullOrEmpty(otm.Amount) && decimal.TryParse(otm.Amount, out var parsedAmt))
                            {
                                itemTotalPrice = parsedAmt;
                            }

                            decimal displayAmount = (actionStatus == "Deferred" || actionStatus == "ReceivedFull") 
                                ? (paidAmount > 0 ? paidAmount : itemTotalPrice) 
                                : itemTotalPrice;

                            string statusLabel = actionStatus switch
                            {
                                "ReceivedFull" => "รับของครบแล้ว",
                                "Deferred" => "ผ่อนชำระ",
                                "Skipped" => "ยังไม่รับของ",
                                _ => actionStatus
                            };

                            string deferredFromText = "-";
                            string carriedOverFromText = "-";

                            if (priorAction != null)
                            {
                                if (priorAction.Action == "Deferred")
                                {
                                    deferredFromText = "ผ่อนมาจาก " + ConvertKeyToThaiMonth(priorAction.MonthYear, shortYear: true);
                                }
                                else if (priorAction.Action == "Skipped")
                                {
                                    carriedOverFromText = "ค้างมาจาก " + ConvertKeyToThaiMonth(priorAction.MonthYear, shortYear: true);
                                }
                            }

                            int qty = !string.IsNullOrWhiteSpace(otm.RemarksQuantity) && int.TryParse(otm.RemarksQuantity, out int q1) ? q1 : 1;

                            // 12 คอลัมน์สำหรับ การลงบันทึกประจำเดือน (Action Sheet)
                            formattedActionItems.Add((orderMonthKey, new object?[]
                            {
                                timestampNow,          // A วัน/เวลาที่บันทึกจริง
                                requestDateDisplay,    // B วันที่ขออนุมัติ
                                approvedDateDisplay,   // C วันที่อนุมัติ
                                poNo,                  // D รหัสใบขออนุมัติ
                                orderName,             // E ชื่อสินค้า
                                qty,                   // F จำนวน
                                dept,                  // G แผนก
                                deliveryTarget,        // H กำหนดส่งมอบ
                                displayAmount,         // I ยอดสั่งซื้อเต็ม
                                statusLabel,           // J สถานะการรับของ
                                deferredFromText,      // K ผ่อนชำระมาจาก
                                carriedOverFromText    // L ยกยอดมาจาก
                            }));
                        }

                        // จัดกลุ่มแยกตามงวดบัญชี
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
                        var content = new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8,
                            "application/json");

                        var response = await client.PostAsync(appScriptUrl, content);
                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"Google Sheets Backup Sync Error: {response.StatusCode}");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"Successfully backed up monthly cost actions & plans to Google Sheets for {monthYearKey}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending backup to Google Sheets: {ex.Message}");
                }
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
            ViewData["HeaderTitle"] = "สรุปค่าใช้จ่ายประจำเดือน";

            var thaiMonths = new[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            var now = _dateTimeProvider.Now;
            var selectedYear = year ?? now.Year;

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

            // Use MonthYear string prefix ("yyyy-MM") instead of CreatedAt to avoid timezone issues
            var yearPrefix = $"{selectedYear:0000}-";
            var prevDecKey = $"{selectedYear - 1}-12";

            var allActions = await _context.MonthlyOrderActions
                .Where(a => a.MonthYear.StartsWith(yearPrefix) || a.MonthYear == prevDecKey)
                .ToListAsync();

            var rows = new List<MonthlySummaryRow>();
            decimal cumulative = 0;

            for (int m = 1; m <= 12; m++)
            {
                var key = $"{selectedYear:0000}-{m:00}";
                var display = $"{thaiMonths[m]} {selectedYear + 543}";
                var monthActions = allActions.Where(a => a.MonthYear == key).ToList();

                var prevKey = m == 1 ? prevDecKey : $"{selectedYear:0000}-{m - 1:00}";
                var carryOverActions = allActions.Where(a => a.MonthYear == prevKey && a.Action == "Deferred").ToList();

                // เช็คว่ารายการ carry-over เหล่านั้น ยังไม่มี action ในเดือนนี้
                // ถ้ามี action แล้ว (ผ่อนต่อ/รับแล้ว) ก็ไม่ควรนับเป็น carry-over ของเดือนนี้
                var thisMonthActionedIds = monthActions.Select(a => a.OrderTrackingMasterId).ToHashSet();
                var activeCarryOvers = carryOverActions
                    .Where(a => !thisMonthActionedIds.Contains(a.OrderTrackingMasterId))
                    .ToList();

                var carryOverCount = activeCarryOvers.Count;
                var carryOverAmount = activeCarryOvers.Sum(a => a.ActionPrice);

                var received = monthActions.Where(a => a.Action == "ReceivedFull").Sum(a => a.ActionPrice);
                var deferred = monthActions.Where(a => a.Action == "Deferred").Sum(a => a.ActionPrice);

                cumulative += received;

                rows.Add(new MonthlySummaryRow
                {
                    MonthKey = key,
                    MonthDisplay = display,
                    ReceivedCount = monthActions.Count(a => a.Action == "ReceivedFull"),
                    ReceivedAmount = received,
                    DeferredCount = monthActions.Count(a => a.Action == "Deferred"),
                    DeferredAmount = deferred,
                    SkippedCount = monthActions.Count(a => a.Action == "Skipped"),
                    TotalPaid = received,
                    Cumulative = cumulative,
                    CarryOverDeferredCount = carryOverCount,
                    CarryOverDeferredAmount = carryOverAmount,
                    HasData = monthActions.Any()
                });
            }

            var latestActionsByOrder = allActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .Select(g => g.OrderByDescending(a => a.MonthYear).ThenByDescending(a => a.CreatedAt).First())
                .ToList();

            var currentDebt = latestActionsByOrder.Where(a => a.Action == "Deferred" || a.Action == "Skipped")
                .Sum(a => a.ActionPrice);

            var uniqueDeferredThisYear = allActions
                .Where(a => a.MonthYear.StartsWith(yearPrefix) && a.Action == "Deferred")
                .GroupBy(a => a.OrderTrackingMasterId)
                .Select(g => g.First())
                .Sum(a => a.ActionPrice);

            var vm = new MonthlySummaryViewModel
            {
                Year = selectedYear,
                Rows = rows,
                GrandTotal = rows.Sum(r => r.TotalPaid),
                TotalReceived = rows.Sum(r => r.ReceivedAmount),
                TotalDeferred = uniqueDeferredThisYear,
                CurrentDebt = currentDebt
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

        // GET: /MonthlyCost/ExportDetailExcel?monthYear=มกราคม+2569
        [HttpGet]
        public async Task<IActionResult> ExportDetailExcel(string monthYear)
        {
            if (string.IsNullOrEmpty(monthYear))
                return BadRequest("monthYear parameter is required");

            var monthYearKey = ConvertThaiMonthToKey(monthYear);

            // Register Mock Graphic Engine for ClosedXML
            ClosedXML.Excel.LoadOptions.DefaultGraphicEngine = new MockGraphicEngine();

            // 1. ดึงรายการที่บันทึกแล้วในเดือนนี้
            var existingActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .ThenInclude(o => o.MatchedInWeeklyPlans)
                .ThenInclude(w => w.WeeklyPlan)
                .Where(moa => moa.MonthYear == monthYearKey)
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

            var carryOverIds = latestPriorActionMap
                .Where(kvp =>
                {
                    if (kvp.Value.Action == "Skipped" || kvp.Value.Action == "Deferred")
                    {
                        var pp = kvp.Value.MonthYear.Split('-');
                        var tp = monthYearKey.Split('-');
                        if (pp.Length == 2 && tp.Length == 2
                                           && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                           && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                        {
                            return ((tY - pY) * 12) + (tM - pM) == 1; // เฉพาะเดือนถัดไปเท่านั้น
                        }
                    }

                    return false;
                })
                .Select(kvp => kvp.Key)
                .ToHashSet();

            var existingActionIds = existingActions.Select(ea => ea.OrderTrackingMasterId).ToHashSet();

            // 3. ดึงรายการรอดำเนินการในเดือนนี้
            var pendingOrdersRaw = await _context.OrderTrackingMasters
                .Include(o => o.MatchedInWeeklyPlans)
                .ThenInclude(w => w.WeeklyPlan)
                .Where(otm => !existingActionIds.Contains(otm.Id))
                .ToListAsync();

            var pendingOrders = pendingOrdersRaw
                .Where(otm =>
                {
                    if (latestPriorActionMap.TryGetValue(otm.Id, out var prior) && prior.Action == "ReceivedFull")
                        return false;
                    return carryOverIds.Contains(otm.Id);
                })
                .ToList();

            // กรองเฉพาะรายการผ่อนชำระที่มีภาระต้องจ่ายประจำเดือนนี้
            // 1) รายการที่กดผ่อนชำระในเดือนนี้ หรือกดรับสินค้าชำระเต็มโดยที่เป็นการชำระรายการผ่อนยกยอดมา
            var deferredExistingActions = existingActions
                .Where(a => a.Action == "Deferred" ||
                            (a.Action == "ReceivedFull" &&
                             latestPriorActionMap.TryGetValue(a.OrderTrackingMasterId, out var p) &&
                             p.Action == "Deferred"))
                .ToList();

            // 2) รายการรอดำเนินการเฉพาะที่เป็นรายการผ่อนยกยอดมาจากเดือนก่อน
            var deferredPendingOrders = pendingOrders
                .Where(otm =>
                    latestPriorActionMap.TryGetValue(otm.Id, out var priorAction) && priorAction.Action == "Deferred")
                .ToList();

            using var workbook = new ClosedXML.Excel.XLWorkbook();
            var ws = workbook.Worksheets.Add("สรุปยอดผ่อนชำระประจำเดือน");

            var fontName = "Noto Sans Thai";
            var colorBorder = ClosedXML.Excel.XLColor.FromHtml("#CBD5E1");

            // Title
            ws.Cell(1, 1).Value = $"รายงานสรุปยอดเงินผ่อนชำระที่ต้องจ่าย — {monthYear}";
            ws.Cell(1, 1).Style.Font.FontName = fontName;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.Black;
            ws.Range(1, 1, 1, 11).Merge();

            // Sub-info
            ws.Cell(2, 1).Value = $"วันที่ส่งออกรายงาน: {DateTime.Now:dd/MM/yyyy HH:mm}    " +
                                  $"รายการผ่อนชำระที่บันทึกแล้ว: {deferredExistingActions.Count} รายการ    " +
                                  $"รายการผ่อนชำระรอดำเนินการ: {deferredPendingOrders.Count} รายการ";
            ws.Cell(2, 1).Style.Font.FontName = fontName;
            ws.Cell(2, 1).Style.Font.FontSize = 10;
            ws.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#475569");
            ws.Range(2, 1, 2, 11).Merge();

            ws.Row(3).Height = 8;

            // Header row (No fill color)
            int headerRow = 4;
            var headers = new[]
            {
                "ลำดับ", "เลขที่อนุมัติ (PO)", "ชื่อสินค้า / รายการอะไหล่", "ปภ.ความเร่งด่วน", "แผนก / หน่วยงาน",
                "เป้าหมายส่งมอบ",
                "ที่มาของรายการ", "สถานะการผ่อนชำระ", "จำนวน", "ยอดเงินผ่อนชำระ (บาท)", "วันที่บันทึก"
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

            ws.Row(headerRow).Height = 22;

            int currentRow = headerRow + 1;
            int itemNo = 1;
            decimal totalPaidAmount = 0m;
            decimal totalPendingAmount = 0m;

            // A. เพิ่มรายการผ่อนชำระที่บันทึกแล้ว
            foreach (var action in deferredExistingActions)
            {
                var otm = action.OrderTrackingMaster;
                string poNumber = otm?.PoNumber ?? "N/A";
                string productName = otm?.Remarks ?? "N/A";

                var latestMatchedPlan = otm?.MatchedInWeeklyPlans?
                    .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                    .FirstOrDefault();

                string department = !string.IsNullOrEmpty(latestMatchedPlan?.Department)
                    ? latestMatchedPlan.Department
                    : "-";
                string urgency = !string.IsNullOrEmpty(otm?.Urgency) ? otm.Urgency : "-";
                string deliveryTarget = latestMatchedPlan?.DeliveryTarget ?? "-";

                string sourceText = "ผ่อนชำระประจำเดือน";
                if (latestPriorActionMap.TryGetValue(action.OrderTrackingMasterId, out var prior) &&
                    prior.Action == "Deferred")
                {
                    sourceText = $"ผ่อนยกยอดมาจาก {ConvertKeyToThaiMonth(prior.MonthYear)}";
                }

                string actionText = action.Action == "ReceivedFull"
                    ? "ชำระยอดผ่อนครบแล้ว"
                    : "ผ่อนชำระ (ตั้งยอดเดือนนี้)";

                string quantity = !string.IsNullOrEmpty(otm?.RemarksQuantity) ? otm.RemarksQuantity : "1";
                decimal amt = action.ActionPrice > 0
                    ? action.ActionPrice
                    : (decimal.TryParse(otm?.Amount, out var p) ? p : 0m);
                totalPaidAmount += amt;

                ws.Cell(currentRow, 1).Value = itemNo++;
                ws.Cell(currentRow, 2).Value = poNumber;
                ws.Cell(currentRow, 3).Value = productName;
                ws.Cell(currentRow, 4).Value = urgency;
                ws.Cell(currentRow, 5).Value = department;
                ws.Cell(currentRow, 6).Value = deliveryTarget;
                ws.Cell(currentRow, 7).Value = sourceText;
                ws.Cell(currentRow, 8).Value = actionText;
                ws.Cell(currentRow, 9).Value = quantity;
                ws.Cell(currentRow, 10).Value = amt;
                ws.Cell(currentRow, 11).Value = action.CreatedAt.ToString("dd/MM/yyyy HH:mm");

                for (int c = 1; c <= 11; c++)
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
                ws.Cell(currentRow, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 10).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                ws.Row(currentRow).Height = 20;
                currentRow++;
            }

            // B. เพิ่มรายการผ่อนชำระรอดำเนินการ (Deferred Carry-Over ที่รอชำระในเดือนนี้)
            foreach (var otm in deferredPendingOrders)
            {
                string poNumber = otm.PoNumber ?? "N/A";
                string productName = otm.Remarks ?? "N/A";

                var latestMatchedPlan = otm.MatchedInWeeklyPlans?
                    .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                    .FirstOrDefault();

                string department = !string.IsNullOrEmpty(latestMatchedPlan?.Department)
                    ? latestMatchedPlan.Department
                    : "-";
                string urgency = !string.IsNullOrEmpty(otm.Urgency) ? otm.Urgency : "-";
                string deliveryTarget = latestMatchedPlan?.DeliveryTarget ?? "-";

                string sourceText = "ผ่อนยกยอดมา";
                decimal price = decimal.TryParse(otm.Amount, out var p) ? p : 0m;

                if (latestPriorActionMap.TryGetValue(otm.Id, out var priorAction) && priorAction.Action == "Deferred")
                {
                    sourceText = $"ผ่อนยกยอดมาจาก {ConvertKeyToThaiMonth(priorAction.MonthYear)}";
                    if (priorAction.ActionPrice > 0) price = priorAction.ActionPrice;
                }

                string quantity = !string.IsNullOrEmpty(otm.RemarksQuantity) ? otm.RemarksQuantity : "1";
                totalPendingAmount += price;

                ws.Cell(currentRow, 1).Value = itemNo++;
                ws.Cell(currentRow, 2).Value = poNumber;
                ws.Cell(currentRow, 3).Value = productName;
                ws.Cell(currentRow, 4).Value = urgency;
                ws.Cell(currentRow, 5).Value = department;
                ws.Cell(currentRow, 6).Value = deliveryTarget;
                ws.Cell(currentRow, 7).Value = sourceText;
                ws.Cell(currentRow, 8).Value = "รอดำเนินการชำระงวดนี้";
                ws.Cell(currentRow, 9).Value = quantity;
                ws.Cell(currentRow, 10).Value = price;
                ws.Cell(currentRow, 11).Value = "-";

                for (int c = 1; c <= 11; c++)
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
                ws.Cell(currentRow, 4).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 5).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 6).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 7).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 9).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
                ws.Cell(currentRow, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
                ws.Cell(currentRow, 10).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(currentRow, 11).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;

                ws.Row(currentRow).Height = 20;
                currentRow++;
            }

            // Summary row
            currentRow++;
            ws.Cell(currentRow, 8).Value = "รวมยอดผ่อนชำระที่บันทึกแล้ว";
            ws.Cell(currentRow, 8).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 8).Style.Font.Bold = true;
            ws.Cell(currentRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            ws.Cell(currentRow, 10).Value = totalPaidAmount;
            ws.Cell(currentRow, 10).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 10).Style.Font.Bold = true;
            ws.Cell(currentRow, 10).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(currentRow, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            currentRow++;

            ws.Cell(currentRow, 8).Value = "รวมยอดผ่อนชำระที่รอชำระงวดนี้";
            ws.Cell(currentRow, 8).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 8).Style.Font.Bold = true;
            ws.Cell(currentRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            ws.Cell(currentRow, 10).Value = totalPendingAmount;
            ws.Cell(currentRow, 10).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 10).Style.Font.Bold = true;
            ws.Cell(currentRow, 10).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(currentRow, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            currentRow++;

            ws.Cell(currentRow, 8).Value = "รวมยอดเงินผ่อนชำระที่ต้องจ่ายทั้งสิ้นเดือนนี้";
            ws.Cell(currentRow, 8).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 8).Style.Font.Bold = true;
            ws.Cell(currentRow, 8).Style.Font.FontColor = ClosedXML.Excel.XLColor.Black;
            ws.Cell(currentRow, 8).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            ws.Cell(currentRow, 10).Value = totalPaidAmount + totalPendingAmount;
            ws.Cell(currentRow, 10).Style.Font.FontName = fontName;
            ws.Cell(currentRow, 10).Style.Font.Bold = true;
            ws.Cell(currentRow, 10).Style.Font.FontColor = ClosedXML.Excel.XLColor.Black;
            ws.Cell(currentRow, 10).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(currentRow, 10).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
            ws.Range(currentRow, 8, currentRow, 10).Style.Border.OutsideBorder =
                ClosedXML.Excel.XLBorderStyleValues.Thin;
            ws.Range(currentRow, 8, currentRow, 10).Style.Border.OutsideBorderColor = colorBorder;

            // ── Column Widths ─────────────────────────────────────────
            ws.Column(1).Width = 8; // ลำดับ
            ws.Column(2).Width = 22; // เลขที่อนุมัติ (PO)
            ws.Column(3).Width = 45; // ชื่อสินค้า / รายการอะไหล่
            ws.Column(4).Width = 18; // ความเร่งด่วน
            ws.Column(5).Width = 18; // แผนก
            ws.Column(6).Width = 18; // เป้าหมายส่งมอบ
            ws.Column(7).Width = 26; // ที่มาของรายการ
            ws.Column(8).Width = 22; // สถานะการบันทึก
            ws.Column(9).Width = 12; // จำนวน
            ws.Column(10).Width = 22; // ยอดเงินที่ต้องจ่าย
            ws.Column(11).Width = 20; // วันที่บันทึก

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            string cleanMonth = monthYear.Trim().Replace(" ", "_");
            string downloadName = $"สรุปรายการที่ต้องจ่าย_{cleanMonth}.xlsx";
            return File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                downloadName);
        }

        // ─── Time Travel / Date Mocking Endpoints ─────────────────────────────────────
        [HttpPost]
        [HttpGet]
        public IActionResult SetMockDate([FromBody] SetMockDateRequest? model, [FromQuery] string? dateStr)
        {
            var date = model?.DateStr ?? dateStr;
            if (string.IsNullOrWhiteSpace(date) || !DateTime.TryParse(date, out var parsedDate))
            {
                return Json(new { success = false, message = "รูปแบบวันที่ไม่ถูกต้อง" });
            }

            Response.Cookies.Append("MockSystemDate", parsedDate.ToString("yyyy-MM-dd"), new CookieOptions
            {
                Expires = DateTimeOffset.Now.AddDays(7),
                HttpOnly = true,
                IsEssential = true
            });

            return Json(new { success = true, mockedDate = parsedDate.ToString("yyyy-MM-dd") });
        }

        [HttpPost]
        [HttpGet]
        public IActionResult ResetMockDate()
        {
            Response.Cookies.Delete("MockSystemDate");
            return Json(new { success = true });
        }

        private async Task AutoClosePriorMonthsAsync()
        {
            try
            {
                var now = _dateTimeProvider.Now;
                var currentMonthKey = now.ToString("yyyy-MM");

                var allOrders = await _context.OrderTrackingMasters
                    .Include(o => o.MatchedInWeeklyPlans)
                    .ThenInclude(m => m.WeeklyPlan)
                    .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                    .ToListAsync();

                var priorActions = await _context.MonthlyOrderActions.ToListAsync();

                var actionByOrderAndMonth = priorActions
                    .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear))
                    .ToDictionary(g => g.Key, g => g.First());

                var receivedFullOrders = priorActions
                    .Where(a => a.Action == "ReceivedFull")
                    .Select(a => a.OrderTrackingMasterId)
                    .ToHashSet();

                bool hasNewActions = false;

                foreach (var order in allOrders)
                {
                    if (receivedFullOrders.Contains(order.Id)) continue;

                    var approvedDt = ParseThaiDate(order.ApprovedDate);
                    if (!approvedDt.HasValue) continue;

                    string approvedMonthKey = $"{approvedDt.Value.Year:0000}-{approvedDt.Value.Month:02}";

                    // เช็คเฉพาะถ้างวดที่อนุมัติ เป็นงวดในอดีต (ก่อนเดือนปัจจุบัน)
                    if (String.CompareOrdinal(approvedMonthKey, currentMonthKey) < 0)
                    {
                        // ถ้าในงวดอนุมัตินั้น ยังไม่มี action ใดๆ บันทึกอยู่เลย ให้บันทึกเป็น Skipped
                        if (!actionByOrderAndMonth.ContainsKey((order.Id, approvedMonthKey)))
                        {
                            var autoSkip = new MonthlyOrderAction
                            {
                                Id = Guid.NewGuid(),
                                OrderTrackingMasterId = order.Id,
                                MonthYear = approvedMonthKey,
                                Action = "Skipped",
                                ActionPrice = 0,
                                IsForcedPayment = false,
                                DeferredFromMonth = null,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };

                            _context.MonthlyOrderActions.Add(autoSkip);
                            actionByOrderAndMonth[(order.Id, approvedMonthKey)] = autoSkip;
                            hasNewActions = true;
                        }
                    }
                }

                if (hasNewActions)
                {
                    await _context.SaveChangesAsync();
                    await SyncAllActionsToGoogleSheetsAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in AutoClosePriorMonthsAsync: {ex.Message}");
            }
        }

        private async Task SyncAllActionsToGoogleSheetsAsync()
        {
            try
            {
                string? appScriptUrl = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
                if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
                {
                    appScriptUrl = _configuration["GoogleSheets:ArchiveAppScriptUrl"];
                }

                if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                {
                    var allActions = await _context.MonthlyOrderActions
                        .Include(a => a.OrderTrackingMaster)
                        .ThenInclude(o => o!.Report)
                        .Include(a => a.OrderTrackingMaster)
                        .ThenInclude(o => o!.MatchedInWeeklyPlans)
                        .ThenInclude(m => m.WeeklyPlan)
                        .OrderBy(a => a.MonthYear)
                        .ThenBy(a => a.CreatedAt)
                        .ToListAsync();

                    var latestPriorActionMap = allActions
                        .GroupBy(a => a.OrderTrackingMasterId)
                        .ToDictionary(g => g.Key, g => g.First());

                    var timestampNow = DateTime.Now.ToString("dd/MM/yyyy HH:mm", new CultureInfo("th-TH"));
                    var formattedActionItems = new List<(string MonthYear, object?[] Row)>();

                    foreach (var act in allActions)
                    {
                        var otm = act.OrderTrackingMaster;
                        if (otm == null) continue;

                        var latestPlan = otm.MatchedInWeeklyPlans
                            .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                            .FirstOrDefault();

                        string poNo = otm.PoNumber ?? "-";
                        string orderName = !string.IsNullOrWhiteSpace(otm.Remarks) ? otm.Remarks.Trim() : "ไม่ระบุ";
                        string dept = latestPlan?.Department ?? otm.Urgency ?? "-";
                        string deliveryTarget = latestPlan?.DeliveryTarget ?? "-";
                        string requestDateDisplay = otm.RequestDate ?? "-";
                        string approvedDateDisplay = otm.ApprovedDate ?? "-";

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
                            "ReceivedFull" => "รับของครบแล้ว",
                            "Deferred" => "ผ่อนชำระ",
                            "Skipped" => "ยังไม่รับของ",
                            _ => act.Action
                        };

                        string deferredFromText = "-";
                        string carriedOverFromText = "-";

                        if (!string.IsNullOrWhiteSpace(act.DeferredFromMonth))
                        {
                            deferredFromText = "ผ่อนมาจาก " + ConvertKeyToThaiMonth(act.DeferredFromMonth, shortYear: true);
                        }
                        else if (latestPriorActionMap.TryGetValue(otm.Id, out var priorAct) && priorAct.Id != act.Id)
                        {
                            if (priorAct.Action == "Deferred")
                            {
                                deferredFromText = "ผ่อนมาจาก " + ConvertKeyToThaiMonth(priorAct.MonthYear, shortYear: true);
                            }
                            else if (priorAct.Action == "Skipped")
                            {
                                carriedOverFromText = "ค้างมาจาก " + ConvertKeyToThaiMonth(priorAct.MonthYear, shortYear: true);
                            }
                        }

                        int qty = !string.IsNullOrWhiteSpace(otm.RemarksQuantity) && int.TryParse(otm.RemarksQuantity, out int q2) ? q2 : 1;

                        formattedActionItems.Add((act.MonthYear, new object?[]
                        {
                            act.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), // A วัน/เวลาบันทึก
                            requestDateDisplay,   // B วันที่ขออนุมัติ
                            approvedDateDisplay,  // C วันที่อนุมัติ
                            poNo,                 // D รหัสใบขออนุมัติ
                            orderName,            // E ชื่อสินค้า
                            qty,                  // F จำนวน
                            dept,                 // G แผนก
                            deliveryTarget,       // H กำหนดส่งมอบ
                            displayAmount,        // I ยอดสั่งซื้อเต็ม
                            statusLabel,          // J สถานะการรับของ (ภาษาไทย)
                            deferredFromText,     // K ผ่อนชำระมาจาก
                            carriedOverFromText   // L ยกยอดมาจาก
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
                    var content =
                        new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                    await client.PostAsync(appScriptUrl, content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SyncAllActionsToGoogleSheets Error: {ex.Message}");
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
}
