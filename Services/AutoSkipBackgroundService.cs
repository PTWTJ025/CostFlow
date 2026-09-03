using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using CostFlow.Services;

namespace CostFlow.Services
{
    /// <summary>
    /// Background Service สำหรับ Auto-Skip รายการที่ยังไม่ได้ action
    /// รันทุกวันเวลา 00:00 โดยอัตโนมัติ
    /// </summary>
    public class AutoSkipBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoSkipBackgroundService> _logger;

        public AutoSkipBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<AutoSkipBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 Auto-Skip Background Service เริ่มทำงาน");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
                        var now = dateTimeProvider.Now;
                        
                        // คำนวณเวลาที่ต้องรอจนถึง 00:00 ของวันถัดไป
                        var tomorrow = now.Date.AddDays(1); // วันถัดไป เวลา 00:00
                        var delay = tomorrow - now;

                        _logger.LogInformation(
                            $"⏰ Auto-Skip ครั้งถัดไป: {tomorrow:dd/MM/yyyy HH:mm:ss} (อีก {delay.TotalHours:F1} ชั่วโมง)");

                        // รอจนถึง 00:00
                        await Task.Delay(delay, stoppingToken);

                        // ถึง 00:00 แล้ว → รัน auto-skip
                        _logger.LogInformation($"🔄 กำลังรัน Auto-Skip... (เวลา: {dateTimeProvider.Now:dd/MM/yyyy HH:mm:ss})");
                        
                        await RunAutoSkipAsync(scope);

                        _logger.LogInformation("✅ Auto-Skip เสร็จสมบูรณ์");
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("⏹️ Auto-Skip Background Service หยุดทำงาน");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"❌ Auto-Skip Error: {ex.Message}");
                    // รอ 1 นาทีแล้วลองใหม่
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private async Task RunAutoSkipAsync(IServiceScope scope)
        {
            // ดึง services ที่จำเป็น
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
            var syncService = scope.ServiceProvider.GetRequiredService<MonthlyOrderSyncService>();

            var now = dateTimeProvider.Now;

            // ดึงข้อมูล orders ทั้งหมด
            var allOrders = await context.OrderTrackingMasters
                .Include(o => o.MatchedInWeeklyPlans)
                .ThenInclude(m => m.WeeklyPlan)
                .Where(o => !string.IsNullOrEmpty(o.ApprovedDate))
                .ToListAsync();

            var priorActions = await context.MonthlyOrderActions.ToListAsync();

            var actionByOrderAndMonth = priorActions
                .GroupBy(a => (a.OrderTrackingMasterId, a.MonthYear.Length > 7 ? a.MonthYear.Substring(0, 7) : a.MonthYear))
                .ToDictionary(g => g.Key, g => g.First());

            var receivedFullOrders = priorActions
                .Where(a => a.Action == "ReceivedFull")
                .Select(a => a.OrderTrackingMasterId)
                .ToHashSet();

            bool hasNewActions = false;
            var affectedMonths = new HashSet<string>();
            int totalAutoSkipped = 0;

            foreach (var order in allOrders)
            {
                if (receivedFullOrders.Contains(order.Id)) continue;

                var approvedDt = ParseThaiDate(order.ApprovedDate);
                if (!approvedDt.HasValue) continue;

                DateTime loopDate = new DateTime(approvedDt.Value.Year, approvedDt.Value.Month, 1);
                DateTime currentDate = new DateTime(now.Year, now.Month, 1);

                // วนลูปสร้าง Skipped ให้ทุกเดือนที่ขาดหายไป
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

                        context.MonthlyOrderActions.Add(autoSkip);
                        actionByOrderAndMonth[(order.Id, loopMonthKey)] = autoSkip;
                        affectedMonths.Add(loopMonthKey);
                        hasNewActions = true;
                        totalAutoSkipped++;
                    }
                    loopDate = loopDate.AddMonths(1);
                }
            }

            if (hasNewActions)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation(
                    $"💾 บันทึก {totalAutoSkipped} รายการใน {affectedMonths.Count} เดือนลงฐานข้อมูล: {string.Join(", ", affectedMonths)}");
            }
            else
            {
                _logger.LogInformation("✓ ไม่มีรายการที่ต้อง auto-skip");
            }
        }

        private DateTime? ParseThaiDate(string? dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return null;
            
            // ลองแปลง format ต่างๆ
            var formats = new[] {
                "dd/MM/yyyy HH:mm:ss",
                "dd/MM/yyyy HH:mm",
                "dd/MM/yyyy",
                "d/M/yyyy",
                "yyyy-MM-dd"
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(dateStr, format, 
                    System.Globalization.CultureInfo.InvariantCulture, 
                    System.Globalization.DateTimeStyles.None, 
                    out var dt))
                {
                    return dt;
                }
            }

            // ลอง parse แบบปกติ
            if (DateTime.TryParse(dateStr, out var result))
            {
                return result;
            }

            return null;
        }
    }
}
