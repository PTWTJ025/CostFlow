using System.IO;
using System.Threading.Tasks;

namespace CostFlow.Services
{
    public interface ISupabaseStorageService
    {
        Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string? folder = "items/general", string? prefix = null);
        Task<bool> DeleteFileAsync(string filePathOrUrl);
        Task<bool> PingKeepAliveAsync();
        Task<bool> PingAssetHubKeepAliveAsync();
        Task<bool> TriggerImageKeepAlivePingAsync();
        bool ShouldPingToday();
        Task SyncPlaceholdersAsync(string webRootPath);
    }
}
