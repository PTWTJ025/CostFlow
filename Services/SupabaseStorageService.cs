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

        private static readonly string[] CheekyMessages = new[]
        {
            "ตื่นยัง? CostFlow แวะมากดกริ่งแล้ววิ่งหนี 🔔🏃",
            "เหงาไหม Supabase? แวะมาทักทายเฉยๆ อย่าเพิ่งหลับนะ ☕",
            "ตรวจเวรยาม: ยาม Supabase ยังอยู่ดีไหม? 👮",
            "ยังไม่ถึง 7 วันหรอก แค่คิดถึงเลยแวะมาปลุก 💖",
            "ตื่นมารับแคลเซียมก่อน ห้ามจำศีลเด็ดขาด! 🥛",
            "Wakey Wakey Eggs and Bakey! Don't sleep bro! 🍳",
            "กาแฟสักแก้วไหมครับ? อย่าเพิ่งหลับนะ CostFlow กำลังดูอยู่ 👀",
            "อย่าเพิ่งนอน! เจ้านายกำลังตรวจงานอยู่ 💼"
        };

        public async Task<bool> PingKeepAliveAsync()
        {
            if (string.IsNullOrWhiteSpace(_supabaseUrl) || string.IsNullOrWhiteSpace(_secretKey))
            {
                return false;
            }

            try
            {
                // สุ่มข้อความกวนๆ สำหรับรอบนี้
                var cheekyMsg = CheekyMessages[Random.Shared.Next(CheekyMessages.Length)];
                var urlSafeMsg = Uri.EscapeDataString(cheekyMsg);

                // 1. ยิง GET Bucket พร้อมแนบ Query String และ User-Agent กวนๆ ให้เห็นใน Log
                var bucketUrl = $"{_supabaseUrl}/storage/v1/bucket?knock_knock=whos_there&wake_up=true&note={urlSafeMsg}";
                using var request = new HttpRequestMessage(HttpMethod.Get, bucketUrl);
                request.Headers.Add("apikey", _secretKey);
                request.Headers.Add("Authorization", $"Bearer {_secretKey}");
                request.Headers.TryAddWithoutValidation("User-Agent", "Supabase-Sleep-Police/1.0 (Wakey-Wakey-Dont-Sleep)");
                request.Headers.TryAddWithoutValidation("X-Wake-Up-Call", urlSafeMsg);

                var response = await _httpClient.SendAsync(request);

                // 2. แอบหยอดจดหมายปลุกน่ารักๆ ทับไว้ใน Bucket 'stock image/wake_up_call.txt'
                try
                {
                    var encodedBucket = Uri.EscapeDataString(_bucket);
                    var wakeFileUrl = $"{_supabaseUrl}/storage/v1/object/{encodedBucket}/wake_up_call.txt";
                    using var fileReq = new HttpRequestMessage(HttpMethod.Post, wakeFileUrl);
                    fileReq.Headers.Add("apikey", _secretKey);
                    fileReq.Headers.Add("Authorization", $"Bearer {_secretKey}");
                    fileReq.Headers.Add("x-upsert", "true");

                    var noteContent = $"🔔 [CostFlow Wake-Up Call]\nข้อความ: {cheekyMsg}\nเวลาปลุกล่าสุด: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC (เวลาไทย: {DateTime.UtcNow.AddHours(7):yyyy-MM-dd HH:mm:ss})\nสถานะ: ตื่นอยู่ตลอดเวลา ห้ามหลับนะ Supabase!";
                    fileReq.Content = new StringContent(noteContent, Encoding.UTF8, "text/plain");
                    await _httpClient.SendAsync(fileReq);
                }
                catch (Exception fileEx)
                {
                    _logger.LogDebug(fileEx, "Wake note write skipped: {Message}", fileEx.Message);
                }

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("🟢 [Supabase Keep-Alive] แกล้งทักสำเร็จ: \"{Message}\" (Supabase ตื่นอยู่ 100%)", cheekyMsg);
                    return true;
                }
                else
                {
                    _logger.LogWarning("🟡 [Supabase Keep-Alive] Ping returned status code: {StatusCode}", response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔴 [Supabase Keep-Alive] Failed to ping Supabase: {Message}", ex.Message);
                return false;
            }
        }
    }
}
