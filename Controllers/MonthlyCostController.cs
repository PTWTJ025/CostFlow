using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using System.Globalization;

namespace CostFlow.Controllers
{
    [Authorize]
    public class MonthlyCostController : Controller
    {
        private readonly AppDbContext _context;

        public MonthlyCostController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /MonthlyCost
        public async Task<IActionResult> Index()
        {
            ViewData["HeaderTitle"] = "คิดค่าใช้จ่ายประจำเดือน";

            // Thai month names (index = month number)
            var thaiMonths = new string[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            // Current month key (Gregorian, e.g. "2026-07")
            var now = DateTime.Now;
            var currentMonthKey = now.ToString("yyyy-MM");
            var currentMonthDisplay = $"{thaiMonths[now.Month]} {now.Year + 543}";

            // Collect all distinct MonthYear keys that have saved actions
            var savedMonthKeys = await _context.MonthlyOrderActions
                .Select(moa => moa.MonthYear)
                .Distinct()
                .ToListAsync();

            // Always include current month and next 3 future months (August, September, October) for testing Deferred & Skipped carry-overs
            var futureMonthKeys = new[]
            {
                currentMonthKey,
                now.AddMonths(1).ToString("yyyy-MM"),
                now.AddMonths(2).ToString("yyyy-MM"),
                now.AddMonths(3).ToString("yyyy-MM")
            };

            var allMonthKeys = savedMonthKeys
                .Union(futureMonthKeys)
                .OrderBy(k => k)
                .ToList();

            // Load all actions for these months in one query
            var allActions = await _context.MonthlyOrderActions
                .Where(moa => allMonthKeys.Contains(moa.MonthYear))
                .ToListAsync();

            // Count pending items per month: orders not yet actioned in that month
            // "Pending" = orders with no action OR action that is not ReceivedFull/Deferred/Skipped
            // We approximate: total unique orders that have ever appeared minus those with actions in that month
            // Simpler: pending = items in that month's context that are still un-actioned
            // Since Index doesn't load full pending lists, we count actions per month and
            // total across all order tracking masters (same logic as Detail)
            var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();

            // Build cards
            var cards = new List<MonthlyCardViewModel>();

            foreach (var key in allMonthKeys)
            {
                // Parse key to display
                var parts = key.Split('-');
                string displayName;
                if (parts.Length == 2 && int.TryParse(parts[0], out var yr) && int.TryParse(parts[1], out var mo) && mo >= 1 && mo <= 12)
                    displayName = $"{thaiMonths[mo]} {yr + 543}";
                else
                    displayName = key;

                var monthActions = allActions.Where(a => a.MonthYear == key).ToList();

                var receivedActions = monthActions.Where(a => a.Action == "ReceivedFull").ToList();
                var deferredActions = monthActions.Where(a => a.Action == "Deferred").ToList();
                var skippedActions  = monthActions.Where(a => a.Action == "Skipped").ToList();

                int receivedCount  = receivedActions.Count;
                int deferredCount  = deferredActions.Count;
                int skippedCount   = skippedActions.Count;
                int doneItems      = receivedCount + deferredCount; // Skipped ยังไม่นับ "เสร็จ"

                decimal receivedAmount = receivedActions.Sum(a => a.ActionPrice);
                decimal deferredAmount = deferredActions.Sum(a => a.ActionPrice);
                decimal skippedAmount  = skippedActions.Sum(a => a.ActionPrice);

                // Carry-over sub-counts:
                // An action is "carry-over" if the same order had a Deferred or Skipped action in a PRIOR month
                // We use IsForcedPayment flag for Deferred carry-overs (set during SaveActions),
                // and for Skipped/ReceivedFull we check prior actions in allActions
                var priorMonthActionMap = allActions
                    .Where(a => string.Compare(a.MonthYear, key) < 0)
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.MonthYear).ThenByDescending(x => x.CreatedAt).First());

                var receivedActionIds = receivedActions.Select(a => a.OrderTrackingMasterId).ToHashSet();
                var deferredActionIds = deferredActions.Select(a => a.OrderTrackingMasterId).ToHashSet();
                var skippedActionIds  = skippedActions.Select(a => a.OrderTrackingMasterId).ToHashSet();

                int receivedCarryOver = receivedActions.Count(a =>
                    priorMonthActionMap.TryGetValue(a.OrderTrackingMasterId, out var prior) &&
                    (prior.Action == "Deferred" || prior.Action == "Skipped"));

                int deferredCarryOver = deferredActions.Count(a =>
                    priorMonthActionMap.TryGetValue(a.OrderTrackingMasterId, out var prior) &&
                    (prior.Action == "Deferred" || prior.Action == "Skipped"));

                int skippedCarryOver = skippedActions.Count(a =>
                    priorMonthActionMap.TryGetValue(a.OrderTrackingMasterId, out var prior) &&
                    (prior.Action == "Deferred" || prior.Action == "Skipped"));

                // Total = actioned items (all 3 types) for this month
                int totalActioned = monthActions.Count;

                // Pending = items that are in this month's context but not yet actioned
                // We calculate by looking at prior ReceivedFull counts to see how many are "closed"
                var priorReceivedFull = await _context.MonthlyOrderActions
                    .Where(moa => string.Compare(moa.MonthYear, key) <= 0 && moa.Action == "ReceivedFull")
                    .Select(moa => moa.OrderTrackingMasterId)
                    .Distinct()
                    .CountAsync();

                int pendingItems  = Math.Max(0, totalOrdersEver - priorReceivedFull - (skippedCount + deferredCount));
                int totalItems    = totalActioned + pendingItems;

                // Determine status
                string statusCode, statusLabel;
                if (pendingItems == 0 && totalItems > 0)
                {
                    statusCode  = "completed";
                    statusLabel = "เสร็จสมบูรณ์";
                }
                else if (key == currentMonthKey)
                {
                    statusCode  = "active";
                    statusLabel = "กำลังดำเนินการ";
                }
                else
                {
                    statusCode  = "pending";
                    statusLabel = "ค้างดำเนินการ";
                }

                // Get associated report file name (most recent action's report, or latest report overall for current month)
                var reportName = string.Empty;
                if (monthActions.Any())
                {
                    var latestActionOrderId = monthActions
                        .OrderByDescending(a => a.CreatedAt)
                        .First().OrderTrackingMasterId;
                    var report = await _context.OrderTrackingMasters
                        .Where(o => o.Id == latestActionOrderId)
                        .Select(o => o.Report.ReportName)
                        .FirstOrDefaultAsync();
                    reportName = report ?? string.Empty;
                }
                else if (key == currentMonthKey)
                {
                    reportName = await _context.OrderTrackingMasters
                        .OrderByDescending(o => o.CreatedAt)
                        .Select(o => o.Report.ReportName)
                        .FirstOrDefaultAsync() ?? string.Empty;
                }

                cards.Add(new MonthlyCardViewModel
                {
                    MonthYearDisplay = displayName,
                    MonthYearKey     = key,
                    FileName         = reportName,
                    StatusCode       = statusCode,
                    StatusLabel      = statusLabel,
                    TotalItems       = totalItems,
                    DoneItems        = doneItems,
                    PendingItems     = pendingItems,
                    ReceivedCount    = receivedCount,
                    ReceivedAmount   = receivedAmount,
                    DeferredCount    = deferredCount,
                    DeferredAmount   = deferredAmount,
                    SkippedCount     = skippedCount,
                    SkippedAmount    = skippedAmount,
                    ReceivedCarryOver = receivedCarryOver,
                    DeferredCarryOver = deferredCarryOver,
                    SkippedCarryOver  = skippedCarryOver,
                });
            }

            var viewModel = new MonthlyCostIndexViewModel { Cards = cards };
            return View(viewModel);
        }

        // GET: /MonthlyCost/Detail/{id}
        public async Task<IActionResult> Detail(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                id = "กรกฎาคม 2569";
            }

            // Parse month/year from Thai format (e.g., "กรกฎาคม 2569")
            var monthYearKey = ConvertThaiMonthToKey(id);

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
            // For Deferred: only carry over if the prior action is from the IMMEDIATELY preceding month
            // For Skipped: carry over from any prior month (can skip multiple months)
            var carryOverIds = latestPriorActionMap
                .Where(kvp =>
                {
                    if (kvp.Value.Action == "Skipped") return true;
                    if (kvp.Value.Action == "Deferred")
                    {
                        // Only carry over Deferred if it's from exactly 1 month before this month
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
                monthEnd   = monthStart.AddMonths(1);
            }

            // Fetch orders that either:
            //   (a) belong to THIS month (Report uploaded in this month), OR
            //   (b) are carry-overs (Deferred / Skipped from a prior month)
            // — and have not yet been actioned in this month
            var allPendingOrders = await _context.OrderTrackingMasters
                .Include(otm => otm.Report)
                .Include(otm => otm.MatchedInWeeklyPlans)
                    .ThenInclude(wpd => wpd.WeeklyPlan)
                .Where(otm =>
                    !existingActionIds.Contains(otm.Id) &&
                    (
                        carryOverIds.Contains(otm.Id) ||
                        (otm.Report.CreatedAt >= monthStart && otm.Report.CreatedAt < monthEnd)
                    ))
                .OrderBy(otm => otm.PoNumber)
                .ToListAsync();

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
                    if (priorAction.Action == "Deferred")
                    {
                        forwardedStatus = "Deferred";
                        forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                        // สำหรับสินค้าที่ผ่อนชำระ ใช้ราคาของเดือนก่อนหน้าที่บันทึกไว้
                        if (priorAction.ActionPrice > 0)
                        {
                            amount = priorAction.ActionPrice;
                        }
                    }
                    else if (priorAction.Action == "Skipped")
                    {
                        forwardedStatus = "Skipped";
                        forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
                        // สำหรับสินค้าที่ไม่รับในเดือนก่อน (ยังมีสต็อกอยู่) ใช้ราคาปกติของสินค้า
                    }
                }

                var productName = otm.Remarks ?? "ไม่ระบุ";

                // ดึงเป้าหมายส่งมอบและหน่วยงานจากไฟล์แผนผลิตประจำสัปดาห์ล่าสุดที่จับคู่ได้
                var latestMatchedPlan = otm.MatchedInWeeklyPlans
                    .OrderByDescending(w => w.WeeklyPlan.UploadedAt)
                    .FirstOrDefault();

                var targetDelivery = !string.IsNullOrWhiteSpace(latestMatchedPlan?.DeliveryTarget)
                    ? latestMatchedPlan.DeliveryTarget 
                    : "-";
                var department = !string.IsNullOrEmpty(latestMatchedPlan?.Department) 
                    ? latestMatchedPlan.Department 
                    : (!string.IsNullOrEmpty(otm.Urgency) ? otm.Urgency : "-");

                pendingItems.Add(new PendingOrderItem
                {
                    OrderId = otm.Id,
                    PoNumber = otm.PoNumber,
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
                    OriginalReportName = otm.Report?.ReportName ?? "N/A"
                });
            }

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
                        CreatedAt = moa.CreatedAt
                    };
                })
                .ToList();

            // Calculate stats
            var stats = new MonthlyStats
            {
                TotalOrders = pendingItems.Count + existingActions.Count,
                TotalPlannedAmount = pendingItems.Sum(p => p.TotalPrice) + existingActions.Sum(ea => ea.ActionPrice),
                ProcessedOrders = existingActions.Count,
                ProcessedAmount = existingActions.Where(ea => ea.Action == "ReceivedFull" || ea.Action == "Deferred").Sum(ea => ea.ActionPrice)
            };

            var now = DateTime.Now;
            var currentMonthKey = now.ToString("yyyy-MM");
            var isCurrentMonth = (monthYearKey == currentMonthKey);

            var viewModel = new MonthlyCostDetailViewModel
            {
                MonthYear = id,
                PendingOrders = pendingItems,
                SavedOrders = savedItems,
                Stats = stats,
                IsCurrentMonth = isCurrentMonth
            };

            ViewData["HeaderTitle"] = $"จัดการค่าใช้จ่าย - {id}";
            return View(viewModel);
        }

        private string ConvertThaiMonthToKey(string thaiMonthYear)
        {
            // Parse "กรกฎาคม 2569" -> "2026-07"
            var parts = thaiMonthYear.Split(' ');
            if (parts.Length != 2) return DateTime.Now.ToString("yyyy-MM");

            var thaiMonths = new Dictionary<string, int>
            {
                {"มกราคม", 1}, {"กุมภาพันธ์", 2}, {"มีนาคม", 3}, {"เมษายน", 4},
                {"พฤษภาคม", 5}, {"มิถุนายน", 6}, {"กรกฎาคม", 7}, {"สิงหาคม", 8},
                {"กันยายน", 9}, {"ตุลาคม", 10}, {"พฤศจิกายน", 11}, {"ธันวาคม", 12}
            };

            if (int.TryParse(parts[1], out var buddhistYear) && thaiMonths.TryGetValue(parts[0], out var month))
            {
                var year = buddhistYear - 543; // Convert Buddhist year to Gregorian
                return $"{year:0000}-{month:00}";
            }

            return DateTime.Now.ToString("yyyy-MM");
        }

        private string ConvertKeyToThaiMonth(string key)
        {
            if (string.IsNullOrEmpty(key) || !key.Contains("-")) return key;
            var parts = key.Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month)) return key;

            var thaiMonths = new string[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            if (month < 1 || month > 12) return key;
            return $"{thaiMonths[month]} {year + 543}";
        }

        [HttpPost]
        public async Task<IActionResult> SaveActions([FromBody] SaveActionsRequest request)
        {
            try
            {
                var monthYearKey = ConvertThaiMonthToKey(request.MonthYear);

                // Load prior actions to enforce Deferred rule:
                // An order that was Deferred last month MUST be ReceivedFull this month — cannot Defer again or Skip
                var priorActions = await _context.MonthlyOrderActions
                    .Where(moa => string.Compare(moa.MonthYear, monthYearKey) < 0)
                    .OrderByDescending(moa => moa.MonthYear)
                    .ThenByDescending(moa => moa.CreatedAt)
                    .ToListAsync();

                var latestPriorActionMap = priorActions
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var action in request.Actions)
                {
                    // Enforce: if prior action was Deferred, this action must be ReceivedFull
                    if (latestPriorActionMap.TryGetValue(action.OrderId, out var priorAction)
                        && priorAction.Action == "Deferred")
                    {
                        if (action.Action != "ReceivedFull")
                        {
                            return BadRequest(new
                            {
                                success = false,
                                error = $"รายการที่ผ่อนชำระมาจากเดือนก่อนต้องชำระเต็มในเดือนนี้เท่านั้น"
                            });
                        }

                        // Enforce: Deferred must only carry over to the IMMEDIATELY next month
                        // Parse priorAction.MonthYear and monthYearKey to check they are exactly 1 month apart
                        var priorParts = priorAction.MonthYear.Split('-');
                        var thisParts  = monthYearKey.Split('-');
                        if (priorParts.Length == 2 && thisParts.Length == 2
                            && int.TryParse(priorParts[0], out var pYear) && int.TryParse(priorParts[1], out var pMonth)
                            && int.TryParse(thisParts[0],  out var tYear) && int.TryParse(thisParts[1],  out var tMonth))
                        {
                            var priorDate = new DateTime(pYear, pMonth, 1);
                            var thisDate  = new DateTime(tYear, tMonth, 1);
                            var monthDiff = ((tYear - pYear) * 12) + (tMonth - pMonth);
                            if (monthDiff != 1)
                            {
                                return BadRequest(new
                                {
                                    success = false,
                                    error = $"รายการผ่อนชำระจาก {ConvertKeyToThaiMonth(priorAction.MonthYear)} ต้องถูกชำระในเดือนถัดไปทันที ({ConvertKeyToThaiMonth(priorDate.AddMonths(1).ToString("yyyy-MM"))}) เท่านั้น ไม่สามารถข้ามเดือนได้"
                                });
                            }
                        }
                    }

                    var monthlyAction = new MonthlyOrderAction
                    {
                        Id = Guid.NewGuid(),
                        OrderTrackingMasterId = action.OrderId,
                        MonthYear = monthYearKey,
                        Action = action.Action,
                        ActionPrice = action.Price,
                        IsForcedPayment = priorAction?.Action == "Deferred",
                        DeferredFromMonth = action.Action == "Deferred" ? monthYearKey : null,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.MonthlyOrderActions.Add(monthlyAction);
                }

                await _context.SaveChangesAsync();
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
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

                var carryOverIds = latestPriorActionMap
                    .Where(kvp =>
                    {
                        if (kvp.Value.Action == "Skipped") return true;
                        if (kvp.Value.Action == "Deferred")
                        {
                            var pp = kvp.Value.MonthYear.Split('-');
                            var tp = monthYearKey.Split('-');
                            if (pp.Length == 2 && tp.Length == 2
                                && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                            {
                                return ((tY - pY) * 12) + (tM - pM) == 1;
                            }
                        }
                        return false;
                    })
                    .Select(kvp => kvp.Key)
                    .ToHashSet();

                var kp = monthYearKey.Split('-');
                DateTime gMonthStart = DateTime.MinValue, gMonthEnd = DateTime.MaxValue;
                if (kp.Length == 2 &&
                    int.TryParse(kp[0], out var gYear) &&
                    int.TryParse(kp[1], out var gMonth))
                {
                    gMonthStart = new DateTime(gYear, gMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                    gMonthEnd   = gMonthStart.AddMonths(1);
                }

                var allPendingOrders = await _context.OrderTrackingMasters
                    .Include(otm => otm.Report)
                    .Include(otm => otm.MatchedInWeeklyPlans)
                        .ThenInclude(wpd => wpd.WeeklyPlan)
                    .Where(otm =>
                        !existingActionIds.Contains(otm.Id) &&
                        (
                            carryOverIds.Contains(otm.Id) ||
                            (otm.Report.CreatedAt >= gMonthStart && otm.Report.CreatedAt < gMonthEnd)
                        ))
                    .OrderBy(otm => otm.PoNumber)
                    .ToListAsync();

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
                            if (priorAction.ActionPrice > 0)
                            {
                                amount = priorAction.ActionPrice;
                            }
                        }
                        else if (priorAction.Action == "Skipped")
                        {
                            forwardedStatus = "Skipped";
                            forwardedFromMonth = ConvertKeyToThaiMonth(priorAction.MonthYear);
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
                    var now = DateTime.Now;
                    var currentMonthKey = now.ToString("yyyy-MM");
                    if (action.MonthYear != currentMonthKey)
                    {
                        var totalActionsInMonth = await _context.MonthlyOrderActions.CountAsync(moa => moa.MonthYear == action.MonthYear);
                        var priorReceivedCount = await _context.MonthlyOrderActions
                            .Where(moa => string.Compare(moa.MonthYear, action.MonthYear) < 0 && moa.Action == "ReceivedFull")
                            .Select(moa => moa.OrderTrackingMasterId)
                            .Distinct()
                            .CountAsync();
                        var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();
                        if (totalOrdersEver - priorReceivedCount - totalActionsInMonth <= 0)
                        {
                            return BadRequest(new { success = false, error = "งวดบัญชีนี้ปิดยอด (Completed) เรียบร้อยแล้ว ไม่สามารถย้อนคืนค่าได้" });
                        }
                    }

                    _context.MonthlyOrderActions.Remove(action);
                    await _context.SaveChangesAsync();
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
                        var now = DateTime.Now;
                        var currentMonthKey = now.ToString("yyyy-MM");
                        if (firstMonth != currentMonthKey)
                        {
                            var totalActionsInMonth = await _context.MonthlyOrderActions.CountAsync(moa => moa.MonthYear == firstMonth);
                            var priorReceivedCount = await _context.MonthlyOrderActions
                                .Where(moa => string.Compare(moa.MonthYear, firstMonth) < 0 && moa.Action == "ReceivedFull")
                                .Select(moa => moa.OrderTrackingMasterId)
                                .Distinct()
                                .CountAsync();
                            var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();
                            if (totalOrdersEver - priorReceivedCount - totalActionsInMonth <= 0)
                            {
                                return BadRequest(new { success = false, error = "งวดบัญชีนี้ปิดยอด (Completed) เรียบร้อยแล้ว ไม่สามารถย้อนคืนค่าได้" });
                            }
                        }

                        _context.MonthlyOrderActions.RemoveRange(actions);
                        await _context.SaveChangesAsync();
                    }
                }
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
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
        public List<OrderAction> Actions { get; set; } = new();
    }

    public class OrderAction
    {
        public Guid OrderId { get; set; }
        public string Action { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }
}
