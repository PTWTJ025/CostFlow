using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CostFlow.Services
{
    /// <summary>
    /// Background Service สำหรับส่ง Heartbeat / Ping ไปยัง Supabase ทุกๆ 12 ชั่วโมง
    /// เพื่อป้องกันไม่ให้โปรเจกต์ Free Tier ของ Supabase ถูก Pause (หลับ) หลังจากไม่มีการใช้งาน 7 วัน
    /// </summary>
    public class SupabaseKeepAliveBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SupabaseKeepAliveBackgroundService> _logger;
        private readonly IConfiguration _configuration;

        // รันทุกๆ 12 ชั่วโมง (2 ครั้งต่อวัน ปลอดภัยจากขีดจำกัด 7 วัน 100%)
        private static readonly TimeSpan PingInterval = TimeSpan.FromHours(12);

        public SupabaseKeepAliveBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<SupabaseKeepAliveBackgroundService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var supabaseUrl = _configuration["Supabase:Url"];
            if (string.IsNullOrWhiteSpace(supabaseUrl))
            {
                _logger.LogInformation("ℹ️ [Supabase Keep-Alive] ไม่พบการตั้งค่า Supabase ใน appsettings.json จะข้ามการทำงาน");
                return;
            }

            _logger.LogInformation("🚀 [Supabase Keep-Alive] Background Service เริ่มต้นทำงาน (รอบการ Ping: ทุก 12 ชม.)");

            // รอ 10 วินาทีหลังจากระบบสตาร์ตเครื่องเพื่อให้แอปพลิเคชันพร้อมเต็มที่ก่อนเริ่ม Ping ครั้งแรก
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var storageService = scope.ServiceProvider.GetRequiredService<ISupabaseStorageService>();
                    // 1. ส่ง Heartbeat ปลุก Supabase ของ CostFlow (คลังภาพสต๊อก)
                    _logger.LogInformation("💓 [Supabase Keep-Alive] กำลังส่งสัญญาณ Heartbeat ไปยัง Supabase (CostFlow Stock)...");
                    var success = await storageService.PingKeepAliveAsync();
                    if (success)
                    {
                        _logger.LogInformation("✅ [Supabase Keep-Alive] สัญญาณ Heartbeat สำเร็จ Supabase (CostFlow) ตื่นอยู่ตลอดเวลา (รอบถัดไป: อีก {Hours} ชม.)", PingInterval.TotalHours);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ [Supabase Keep-Alive] สัญญาณ Heartbeat (CostFlow) ไม่สำเร็จ แต่จะพยายามใหม่ในรอบถัดไป");
                    }

                    // 2. ส่ง Heartbeat ปลุก Supabase ของโปรเจกต์ภายนอก: AssetHub
                    _logger.LogInformation("💓 [AssetHub Keep-Alive] กำลังส่งสัญญาณ Heartbeat ไปยัง Supabase (AssetHub)...");
                    var successAssetHub = await storageService.PingAssetHubKeepAliveAsync();
                    if (successAssetHub)
                    {
                        _logger.LogInformation("✅ [AssetHub Keep-Alive] สัญญาณ Heartbeat สำเร็จ Supabase (AssetHub) ตื่นอยู่ตลอดเวลา");
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ [AssetHub Keep-Alive] สัญญาณ Heartbeat (AssetHub) ไม่สำเร็จ แต่จะพยายามใหม่ในรอบถัดไป");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ [Supabase Keep-Alive] เกิดข้อผิดพลาดในการส่งสัญญาณ Heartbeat: {Message}", ex.Message);
                }

                try
                {
                    await Task.Delay(PingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
