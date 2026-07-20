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
        public async Task<IActionResult> Index(int? year)
        {
            ViewData["HeaderTitle"] = "คิดค่าใช้จ่ายประจำเดือน";

            // Thai month names (index = month number)
            var thaiMonths = new[]
            {
                "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
            };

            var now = DateTime.Now;
            var selectedYear = year ?? now.Year; // ค่าเริ่มต้น: ปีปัจจุบัน

            // สร้างรายการปีที่รองรับ (เรียงจากปีล่าสุดลงไปถึงปี พ.ศ. 2565)
            var maxYear = Math.Max(now.Year, 2026); // อย่างน้อยสุดคือปี 2569 (2026)
            var availableYears = new List<int>();
            for (int y = maxYear; y >= 2022; y--) // 2022 = พ.ศ. 2565
            {
                availableYears.Add(y);
            }

            ViewBag.AvailableYears = availableYears;
            ViewBag.SelectedYear = selectedYear;

            // Load all actions for the selected year
            // Use MonthYear string prefix ("yyyy-MM") instead of CreatedAt to avoid timezone issues
            var yearPrefix = $"{selectedYear:0000}-";
            var yearEndPrefix = $"{selectedYear + 1:0000}-";

            var allActions = await _context.MonthlyOrderActions
                .Include(moa => moa.OrderTrackingMaster)
                .ThenInclude(o => o.Report)
                .Where(moa => moa.MonthYear.StartsWith(yearPrefix))
                .ToListAsync();

            var allPriorReceivedList = await _context.MonthlyOrderActions
                .Where(moa => moa.Action == "ReceivedFull" && string.Compare(moa.MonthYear, $"{selectedYear:0000}-12") <= 0)
                .Select(moa => new { moa.MonthYear, moa.OrderTrackingMasterId })
                .ToListAsync();

            var totalOrdersEver = await _context.OrderTrackingMasters.CountAsync();

            // ดึงข้อมูล OrderTrackingMasters ทั้งหมดเพื่อคำนวณยอดรวมของแต่ละเดือน (ก่อน action)
            var allOrders = await _context.OrderTrackingMasters
                .Select(o => new { o.Id, o.Amount, o.ApprovedDate })
                .ToListAsync();

            // Build cards for all 12 months
            var cards = new List<MonthlyCardViewModel>();

            for (int month = 1; month <= 12; month++)
            {
                var monthKey = $"{selectedYear:0000}-{month:00}";
                var displayName = $"{thaiMonths[month]} {selectedYear + 543}";

                // คำนวณยอดรวมของสินค้าในเดือนนี้จาก ApprovedDate (ก่อน action)
                var monthStart = new DateTime(selectedYear, month, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEnd = monthStart.AddMonths(1);
                var totalAmount = allOrders
                    .Where(o =>
                    {
                        var d = ParseThaiDate(o.ApprovedDate);
                        return d.HasValue && d.Value >= monthStart && d.Value < monthEnd;
                    })
                    .Sum(o => decimal.TryParse(o.Amount, out var a) ? a : 0m);

                var monthActions = allActions.Where(a => a.MonthYear == monthKey).ToList();

                // รับสินค้าชำระเต็ม = บันทึกในเดือนนี้เท่านั้น
                var receivedActions = monthActions.Where(a => a.Action == "ReceivedFull").ToList();
                
                // ผ่อนชำระและยังไม่รับ = ไม่นับที่บันทึกในเดือนนี้ แต่นับเฉพาะที่ค้างจากเดือนก่อน
                var skippedActions = monthActions.Where(a => a.Action == "Skipped").ToList();

                int receivedCount = receivedActions.Count;
                int doneItems = receivedCount; // นับเฉพาะที่รับสินค้าแล้วเท่านั้น

                decimal receivedAmount = receivedActions.Sum(a => a.ActionPrice);

                // Carry-over calculations - ดึงข้อมูลจากเดือนก่อนหน้า
                var priorMonthActionMap = allActions
                    .Where(a => string.Compare(a.MonthYear, monthKey) < 0)
                    .GroupBy(a => a.OrderTrackingMasterId)
                    .ToDictionary(g => g.Key,
                        g => g.OrderByDescending(x => x.MonthYear).ThenByDescending(x => x.CreatedAt).First());

                // หารายการที่ยังค้างจากเดือนก่อนหน้า (ยังไม่ได้ดำเนินการในเดือนนี้)
                var existingActionOrderIds = monthActions.Select(ma => ma.OrderTrackingMasterId).ToHashSet();
                
                var pendingCarryOverItems = priorMonthActionMap
                    .Where(kvp => !existingActionOrderIds.Contains(kvp.Key)) // ยังไม่ได้ดำเนินการในเดือนนี้
                    .Where(kvp =>
                    {
                        if (kvp.Value.Action == "Skipped") return true;
                        if (kvp.Value.Action == "Deferred")
                        {
                            // ผ่อนชำระจะแสดงในเดือนถัดไป (ห่างกัน 1 เดือน)
                            var pp = kvp.Value.MonthYear.Split('-');
                            var tp = monthKey.Split('-');
                            if (pp.Length == 2 && tp.Length == 2
                                && int.TryParse(pp[0], out var pY) && int.TryParse(pp[1], out var pM)
                                && int.TryParse(tp[0], out var tY) && int.TryParse(tp[1], out var tM))
                            {
                                var monthDiff = ((tY - pY) * 12) + (tM - pM);
                                return monthDiff == 1; // เฉพาะเดือนถัดไปเท่านั้น
                            }
                        }
                        return false;
                    })
                    .ToList();

                // นับและรวมยอดเงินจากรายการที่ค้างจากเดือนก่อน
                int deferredCount = pendingCarryOverItems.Count(kvp => kvp.Value.Action == "Deferred");
                int skippedCount = skippedActions.Count + pendingCarryOverItems.Count(kvp => kvp.Value.Action == "Skipped");
                
                decimal deferredAmount = pendingCarryOverItems
                    .Where(kvp => kvp.Value.Action == "Deferred")
                    .Sum(kvp => kvp.Value.ActionPrice);

                // Skipped มี ActionPrice = 0 เสมอ ต้องดึงราคาจริงจาก OrderTrackingMaster.Amount
                decimal skippedAmount = skippedActions.Sum(a =>
                {
                    if (a.OrderTrackingMaster != null &&
                        !string.IsNullOrEmpty(a.OrderTrackingMaster.Amount) &&
                        decimal.TryParse(a.OrderTrackingMaster.Amount, out var amt))
                        return amt;
                    return 0m;
                }) + pendingCarryOverItems
                    .Where(kvp => kvp.Value.Action == "Skipped")
                    .Sum(kvp =>
                    {
                        // ดึงราคาจาก OrderTrackingMaster ที่ join มาใน allActions
                        var otm = allActions.FirstOrDefault(a => a.OrderTrackingMasterId == kvp.Key)?.OrderTrackingMaster;
                        if (otm != null && !string.IsNullOrEmpty(otm.Amount) &&
                            decimal.TryParse(otm.Amount, out var amt))
                            return amt;
                        return 0m;
                    });

                // นับจำนวนที่ carry-over มา (+X ค้างเดิม)
                int receivedCarryOver = receivedActions.Count(a =>
                    priorMonthActionMap.TryGetValue(a.OrderTrackingMasterId, out var prior) &&
                    (prior.Action == "Deferred" || prior.Action == "Skipped"));

                int deferredCarryOver = deferredCount; // ทั้งหมดเป็นรายการค้างจากเดือนก่อน

                int skippedCarryOver = pendingCarryOverItems.Count(kvp => kvp.Value.Action == "Skipped");

                int totalActioned = monthActions.Count;

                // Pending calculation (In-memory to avoid N+1)
                var priorReceivedFull = allPriorReceivedList
                    .Where(moa => string.Compare(moa.MonthYear, monthKey) <= 0)
                    .Select(moa => moa.OrderTrackingMasterId)
                    .Distinct()
                    .Count();

                int pendingItems = Math.Max(0, totalOrdersEver - priorReceivedFull - (skippedCount + deferredCount));
                int totalItems = totalActioned + pendingItems;

                // Determine status
                var currentMonthKey = now.ToString("yyyy-MM");
                string statusCode, statusLabel;

                // เปรียบเทียบเดือน
                var isCurrentMonth = monthKey == currentMonthKey;
                var isPastMonth = string.Compare(monthKey, currentMonthKey) < 0;
                var isFutureMonth = string.Compare(monthKey, currentMonthKey) > 0;

                if (isFutureMonth)
                {
                    // เดือนที่ยังไม่ถึง
                    statusCode = "future";
                    statusLabel = "รอดำเนินการ";
                }
                else if (pendingItems == 0 && totalItems > 0)
                {
                    // เสร็จสมบูรณ์
                    statusCode = "completed";
                    statusLabel = "เสร็จสมบูรณ์";
                }
                else if (isCurrentMonth)
                {
                    // เดือนปัจจุบัน
                    statusCode = "active";
                    statusLabel = "กำลังดำเนินการ";
                }
                else
                {
                    // เดือนที่ผ่านมาแต่ยังไม่เสร็จ
                    statusCode = "pending";
                    statusLabel = "ค้างดำเนินการ";
                }

                // Get report name (In-memory to avoid N+1)
                var reportName = string.Empty;
                if (monthActions.Any())
                {
                    var latestAction = monthActions
                        .OrderByDescending(a => a.CreatedAt)
                        .First();
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
                    TotalAmount = totalAmount,
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
            // For Deferred: only carry over if the prior action is from the IMMEDIATELY preceding month (exactly 1 month)
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
                            return monthDiff == 1;  // เฉพาะเดือนถัดไปเท่านั้น
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

            // Filter by ApprovedDate or carry-over
            allPendingOrders = allPendingOrders
                .Where(otm =>
                {
                    if (carryOverIds.Contains(otm.Id))
                        return true;

                    // Parse ApprovedDate และเช็คว่าอยู่ในเดือนนี้หรือไม่
                    var approvedDate = ParseThaiDate(otm.ApprovedDate);
                    if (approvedDate == null)
                        return false;

                    return approvedDate.Value >= monthStart && approvedDate.Value < monthEnd;
                })
                .ToList();

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

                // หาเดือนต้นทางที่สินค้าถูกสร้างครั้งแรก (จากวันที่ในไฟล์ Report)
                var originalMonth = string.Empty;
                if (otm.Report?.CreatedAt != null)
                {
                    var reportDate = otm.Report.CreatedAt;
                    originalMonth = ConvertKeyToThaiMonth($"{reportDate.Year:0000}-{reportDate.Month:00}");
                }

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
                    OriginalReportName = otm.Report?.ReportName ?? "N/A",
                    OriginalMonth = originalMonth
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
            // ยอดรวมของเดือน = เฉพาะสินค้าใหม่ของเดือนนี้เท่านั้น (ไม่รวม carry-over จากเดือนก่อน)
            // carry-over = สินค้าที่เคย Deferred หรือ Skipped มาจากเดือนก่อน
            var totalPlannedAmount = existingActions
                .Where(ea => !latestPriorActionMap.ContainsKey(ea.OrderTrackingMasterId)) // เฉพาะสินค้าใหม่
                .Sum(ea =>
                {
                    if (ea.Action == "ReceivedFull" && ea.ActionPrice > 0)
                        return ea.ActionPrice;
                    if (ea.Action == "Deferred" && ea.ActionPrice > 0)
                        return ea.ActionPrice;
                    // Skipped ใช้ราคาจริงจาก OrderTrackingMaster
                    if (ea.OrderTrackingMaster != null &&
                        !string.IsNullOrEmpty(ea.OrderTrackingMaster.Amount) &&
                        decimal.TryParse(ea.OrderTrackingMaster.Amount, out var realAmt))
                        return realAmt;
                    return 0m;
                });

            // บวกยอด pending ที่ยังไม่ได้บันทึก เฉพาะสินค้าใหม่ของเดือนนี้ (ไม่รวม carry-over)
            totalPlannedAmount += pendingItems
                .Where(p => string.IsNullOrEmpty(p.ForwardedStatus))
                .Sum(p => p.TotalPrice);

            // จ่ายแล้วจริง = เฉพาะ ReceivedFull ของสินค้าใหม่เดือนนี้เท่านั้น (ไม่รวม carry-over ที่รับในเดือนนี้)
            var processedAmount = existingActions
                .Where(ea => ea.Action == "ReceivedFull" &&
                             !latestPriorActionMap.ContainsKey(ea.OrderTrackingMasterId))
                .Sum(ea => ea.ActionPrice);

            var stats = new MonthlyStats
            {
                TotalOrders = pendingItems.Count + existingActions.Count,
                TotalPlannedAmount = totalPlannedAmount,
                ProcessedOrders = existingActions.Count(ea => ea.Action == "ReceivedFull" &&
                                                              !latestPriorActionMap.ContainsKey(ea.OrderTrackingMasterId)),
                ProcessedAmount = processedAmount
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
                { "มกราคม", 1 }, { "กุมภาพันธ์", 2 }, { "มีนาคม", 3 }, { "เมษายน", 4 },
                { "พฤษภาคม", 5 }, { "มิถุนายน", 6 }, { "กรกฎาคม", 7 }, { "สิงหาคม", 8 },
                { "กันยายน", 9 }, { "ตุลาคม", 10 }, { "พฤศจิกายน", 11 }, { "ธันวาคม", 12 }
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
            if (parts.Length != 2 || !int.TryParse(parts[0], out var year) ||
                !int.TryParse(parts[1], out var month)) return key;

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
                    Console.WriteLine($"Fallback - Carry-over: {carryOverIds.Count}, Month Orders: {monthOrderIds.Count}");
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
                        var otm = await _context.OrderTrackingMasters
                            .Where(o => o.Id == action.OrderId)
                            .Select(o => o.Amount)
                            .FirstOrDefaultAsync();
                        if (!string.IsNullOrEmpty(otm) && decimal.TryParse(otm, out var realPrice))
                        {
                            originalPrice = realPrice;
                        }
                        else if (priorAction?.Action == "Deferred" && priorAction.ActionPrice > 0)
                        {
                            originalPrice = priorAction.ActionPrice;
                        }
                    }

                    var monthlyAction = new MonthlyOrderAction
                    {
                        Id = Guid.NewGuid(),
                        OrderTrackingMasterId = action.OrderId,
                        MonthYear = monthYearKey,
                        Action = action.Action,
                        ActionPrice = action.Action == "Deferred" ? originalPrice
                                    : action.Action == "Skipped"  ? 0
                                    : action.Price,
                        IsForcedPayment = false,
                        DeferredFromMonth = action.Action == "Deferred" ? monthYearKey : null,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _context.MonthlyOrderActions.Add(monthlyAction);
                }

                // บันทึกสินค้าที่ไม่ได้เลือกเป็น "Skipped" อัตโนมัติ
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
                                return ((tY - pY) * 12) + (tM - pM) == 1;  // เฉพาะเดือนถัดไปเท่านั้น
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
                    gMonthEnd = gMonthStart.AddMonths(1);
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

            var thaiMonths = new[] { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน",
                "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม",
                "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };

            var now          = DateTime.Now;
            var selectedYear = year ?? now.Year;
            var maxYear      = Math.Max(now.Year, 2026);
            var availableYears = new List<int>();
            for (int y = maxYear; y >= 2022; y--) availableYears.Add(y);

            ViewBag.AvailableYears = availableYears;
            ViewBag.SelectedYear   = selectedYear;

            // Use MonthYear string prefix ("yyyy-MM") instead of CreatedAt to avoid timezone issues
            var yearPrefix = $"{selectedYear:0000}-";
            var prevDecKey = $"{selectedYear - 1}-12";

            var allActions = await _context.MonthlyOrderActions
                .Where(a => a.MonthYear.StartsWith(yearPrefix) || a.MonthYear == prevDecKey)
                .ToListAsync();

            var rows       = new List<MonthlySummaryRow>();
            decimal cumulative = 0;

            for (int m = 1; m <= 12; m++)
            {
                var key     = $"{selectedYear:0000}-{m:00}";
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
                    MonthKey       = key,
                    MonthDisplay   = display,
                    ReceivedCount  = monthActions.Count(a => a.Action == "ReceivedFull"),
                    ReceivedAmount = received,
                    DeferredCount  = monthActions.Count(a => a.Action == "Deferred"),
                    DeferredAmount = deferred,
                    SkippedCount   = monthActions.Count(a => a.Action == "Skipped"),
                    TotalPaid      = received,
                    Cumulative     = cumulative,
                    CarryOverDeferredCount = carryOverCount,
                    CarryOverDeferredAmount = carryOverAmount,
                    HasData        = monthActions.Any()
                });
            }

            var latestActionsByOrder = allActions
                .GroupBy(a => a.OrderTrackingMasterId)
                .Select(g => g.OrderByDescending(a => a.MonthYear).ThenByDescending(a => a.CreatedAt).First())
                .ToList();

            var currentDebt = latestActionsByOrder.Where(a => a.Action == "Deferred" || a.Action == "Skipped").Sum(a => a.ActionPrice);

            var uniqueDeferredThisYear = allActions
                .Where(a => a.MonthYear.StartsWith(yearPrefix) && a.Action == "Deferred")
                .GroupBy(a => a.OrderTrackingMasterId)
                .Select(g => g.First())
                .Sum(a => a.ActionPrice);

            var vm = new MonthlySummaryViewModel
            {
                Year          = selectedYear,
                Rows          = rows,
                GrandTotal    = rows.Sum(r => r.TotalPaid),
                TotalReceived = rows.Sum(r => r.ReceivedAmount),
                TotalDeferred = uniqueDeferredThisYear,
                CurrentDebt   = currentDebt
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
        public List<Guid> AllPendingOrderIds { get; set; } = new(); // เพิ่ม: list ของ pending orders ทั้งหมด
        public List<OrderAction> Actions { get; set; } = new();
    }

    public class OrderAction
    {
        public Guid OrderId { get; set; }
        public string Action { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }
}
