using System;
using System.IO;
using System.Text.Json;
using CostFlow.Models;

namespace CostFlow.Services
{
    public class ImportStorageService
    {
        private readonly string _storageDir;

        public ImportStorageService()
        {
            // Resolve temp folder at project root
            _storageDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp-imports");
            if (!Directory.Exists(_storageDir))
            {
                Directory.CreateDirectory(_storageDir);
            }
        }

        public void SaveImport(ImportedFile file)
        {
            PruneExpiredFiles();

            string filePath = Path.Combine(_storageDir, $"{file.SessionId}.json");
            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            File.WriteAllText(filePath, json);
        }

        public ImportedFile? GetImport(Guid sessionId)
        {
            PruneExpiredFiles();

            string filePath = Path.Combine(_storageDir, $"{sessionId}.json");
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<ImportedFile>(json);
            }
            catch
            {
                return null;
            }
        }

        public void DeleteImport(Guid sessionId)
        {
            string filePath = Path.Combine(_storageDir, $"{sessionId}.json");
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    // Ignore delete errors
                }
            }
        }

        private void PruneExpiredFiles()
        {
            try
            {
                var dirInfo = new DirectoryInfo(_storageDir);
                var files = dirInfo.GetFiles("*.json");
                DateTime cutoff = DateTime.Now.AddMinutes(-30);

                foreach (var file in files)
                {
                    if (file.LastWriteTime < cutoff)
                    {
                        file.Delete();
                    }
                }
            }
            catch
            {
                // Prevent failures in clean-up task from crashing normal flows
            }
        }
    }
}
