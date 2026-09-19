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
                _logger.LogInformation("[Supabase Keep-Alive] Supabase configuration not found in appsettings.json. Skipping service execution.");
                return;
            }

            _logger.LogInformation("[Supabase Keep-Alive] Background Service started. Scheduled interval: every {Hours} hours.", PingInterval.TotalHours);

            // Wait 10 seconds after application startup to ensure application is fully initialized
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

                    // 1. Dispatch Heartbeat to CostFlow Supabase (Stock Images Storage)
                    _logger.LogInformation("[CostFlow Keep-Alive] [Hey HuaNa, wake up and get to work] Dispatching heartbeat to CostFlow Supabase (Target: Stock Images Bucket)...");
                    var success = await storageService.PingKeepAliveAsync();
                    if (success)
                    {
                        _logger.LogInformation("[CostFlow Keep-Alive] [Hey HuaNa, wake up and get to work] Heartbeat acknowledged: CostFlow Supabase is awake and working. Next scheduled ping in {Hours} hours.", PingInterval.TotalHours);
                    }
                    else
                    {
                        _logger.LogWarning("[CostFlow Keep-Alive] [Hey HuaNa, wake up and get to work] Heartbeat was unacknowledged for CostFlow Supabase. Will retry in next scheduled cycle.");
                    }

                    // 2. Dispatch Heartbeat to External Project Supabase: AssetHub (Barcode System)
                    _logger.LogInformation("[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Dispatching heartbeat to AssetHub Supabase (Target: Barcode Assets Bucket)...");
                    var successAssetHub = await storageService.PingAssetHubKeepAliveAsync();
                    if (successAssetHub)
                    {
                        _logger.LogInformation("[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Heartbeat acknowledged: AssetHub Supabase is awake and working. Next scheduled ping in {Hours} hours.", PingInterval.TotalHours);
                    }
                    else
                    {
                        _logger.LogWarning("[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Heartbeat was unacknowledged for AssetHub Supabase. Will retry in next scheduled cycle.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Supabase Keep-Alive] Unexpected error occurred during heartbeat dispatch: {Message}", ex.Message);
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
