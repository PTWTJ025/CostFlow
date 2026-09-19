using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CostFlow.Services
{
    public class SupabaseStorageService : ISupabaseStorageService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SupabaseStorageService> _logger;

        private readonly string _supabaseUrl;
        private readonly string _secretKey;
        private readonly string _bucket;

        public SupabaseStorageService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<SupabaseStorageService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;

            _supabaseUrl = (_configuration["Supabase:Url"] ?? "").Trim().TrimEnd('/');
            _secretKey = (_configuration["Supabase:SecretKey"] ?? _configuration["Supabase:PublishableKey"] ?? "").Trim();
            _bucket = (_configuration["Supabase:StorageBucket"] ?? "stock image").Trim();
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_secretKey))
            {
                throw new InvalidOperationException("Supabase URL หรือ API Key ยังไม่ได้รับการกำหนดค่าใน appsettings.json");
            }

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            var uniqueFileName = $"stock_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}{extension}";
            var encodedBucket = Uri.EscapeDataString(_bucket);
            var uploadUrl = $"{_supabaseUrl}/storage/v1/object/{encodedBucket}/{uniqueFileName}";

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            request.Headers.Add("apikey", _secretKey);
            request.Headers.Add("Authorization", $"Bearer {_secretKey}");
            request.Headers.Add("x-upsert", "true");

            using var content = new StreamContent(fileStream);
            content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Supabase Storage upload failed with status {StatusCode}: {Error}", response.StatusCode, errorBody);
                throw new HttpRequestException($"อัปโหลดรูปภาพขึ้น Supabase Storage ไม่สำเร็จ (สถานะ {response.StatusCode}): {errorBody}");
            }

            var publicUrl = $"{_supabaseUrl}/storage/v1/object/public/{encodedBucket}/{uniqueFileName}";
            _logger.LogInformation("Supabase Storage upload success: {PublicUrl}", publicUrl);
            return publicUrl;
        }

        public async Task<bool> DeleteFileAsync(string filePathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(filePathOrUrl) || string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_secretKey))
            {
                return false;
            }

            try
            {
                // ดึงชื่อไฟล์ออกจาก URL หรือ Path
                var fileName = Path.GetFileName(new Uri(filePathOrUrl).AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return false;
                }

                var encodedBucket = Uri.EscapeDataString(_bucket);
                var deleteUrl = $"{_supabaseUrl}/storage/v1/object/{encodedBucket}";

                using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                request.Headers.Add("apikey", _secretKey);
                request.Headers.Add("Authorization", $"Bearer {_secretKey}");

                var bodyJson = JsonSerializer.Serialize(new { prefixes = new[] { fileName } });
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file from Supabase Storage: {Path}", filePathOrUrl);
                return false;
            }
        }

        public async Task<bool> PingKeepAliveAsync()
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_secretKey))
            {
                _logger.LogWarning("[Supabase Keep-Alive] [Hey HuaNa, wake up and get to work] CostFlow Supabase configuration is missing or incomplete. Skipping ping.");
                return false;
            }

            try
            {
                // Ping CostFlow Supabase Storage with karaoke call and wake up command
                var query = "?msg=Hey-HuaNa-Wake-Up-And-Get-To-Work&project=CostFlow-Stock-System&service=StockImages&action=KeepAlive-Heartbeat&sender=CostFlow-WakeUp-Service";
                var bucketUrl = $"{_supabaseUrl}/storage/v1/bucket{query}";
                using var request = new HttpRequestMessage(HttpMethod.Get, bucketUrl);
                request.Headers.Add("apikey", _secretKey);
                request.Headers.Add("Authorization", $"Bearer {_secretKey}");
                request.Headers.TryAddWithoutValidation("User-Agent", "CostFlow-KeepAlive/2.0 (Hey HuaNa, wake up and get to work! Target: CostFlow-Stock)");
                request.Headers.TryAddWithoutValidation("X-Client-Info", "CostFlow-Stock-Heartbeat/2.0");
                request.Headers.TryAddWithoutValidation("X-WakeUp-Call", "Hey HuaNa, wake up and get to work!");
                request.Headers.TryAddWithoutValidation("X-Project-Target", "CostFlow-Stock-System");

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[Supabase Keep-Alive] [Hey HuaNa, wake up and get to work] Successfully pinged CostFlow Supabase Storage (Status: {StatusCode} OK). Project is awake and working.", (int)response.StatusCode);
                    return true;
                }
                else
                {
                    _logger.LogWarning("[Supabase Keep-Alive] [Hey HuaNa, wake up and get to work] Ping to CostFlow Supabase Storage returned unexpected status: {StatusCode}", response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Supabase Keep-Alive] [Hey HuaNa, wake up and get to work] Failed to ping CostFlow Supabase Storage: {Message}", ex.Message);
                return false;
            }
        }

        public async Task<bool> PingAssetHubKeepAliveAsync()
        {
            var assetHubUrl = _configuration["Supabase:AssetHub:Url"]?.Trim().TrimEnd('/');
            var assetHubApiKey = _configuration["Supabase:AssetHub:ApiKey"]?.Trim();
            var assetHubBucket = _configuration["Supabase:AssetHub:Bucket"]?.Trim() ?? "assets";

            if (string.IsNullOrWhiteSpace(assetHubUrl) || string.IsNullOrWhiteSpace(assetHubApiKey))
            {
                _logger.LogWarning("[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Supabase:AssetHub configuration is missing or incomplete in appsettings.json. Skipping ping.");
                return false;
            }

            try
            {
                // Ping AssetHub Supabase Storage with karaoke call and wake up command
                var query = $"?msg=Hey-HuaNa-Wake-Up-And-Get-To-Work&project=AssetHub-Barcode-System&service=AssetStorage&bucket={assetHubBucket}&sender=CostFlow-WakeUp-Service";
                var bucketUrl = $"{assetHubUrl}/storage/v1/bucket/{assetHubBucket}{query}";
                using var request = new HttpRequestMessage(HttpMethod.Get, bucketUrl);
                request.Headers.Add("apikey", assetHubApiKey);
                request.Headers.Add("Authorization", $"Bearer {assetHubApiKey}");
                request.Headers.TryAddWithoutValidation("User-Agent", "CostFlow-KeepAlive/2.0 (Hey HuaNa, wake up and get to work! Target: AssetHub)");
                request.Headers.TryAddWithoutValidation("X-Client-Info", "CostFlow-AssetHub-Heartbeat/2.0");
                request.Headers.TryAddWithoutValidation("X-WakeUp-Call", "Hey HuaNa, wake up and get to work!");
                request.Headers.TryAddWithoutValidation("X-Project-Target", "AssetHub-Barcode-System");

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Successfully pinged AssetHub Supabase Storage (Status: {StatusCode} OK). Project is awake and working.", (int)response.StatusCode);
                    return true;
                }
                else
                {
                    _logger.LogWarning("[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Ping to AssetHub Supabase Storage returned unexpected status: {StatusCode}", response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AssetHub Keep-Alive] [Hey HuaNa, wake up and get to work] Failed to ping AssetHub Supabase Storage: {Message}", ex.Message);
                return false;
            }
        }
    }
}
