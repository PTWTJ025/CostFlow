using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using CostFlow.Models;
using CostFlow.Services;

namespace CostFlow.Controllers
{
    // Handles temporary file upload and caching for both FileImport and FileMerge workflows
    [Authorize(Roles = "Admin")]
    public class FileImportController : Controller
    {
        private readonly ExcelFileReader _excelReader;
        private readonly CsvFileReader _csvReader;
        private readonly ImportStorageService _storageService;

        public FileImportController()
        {
            _excelReader = new ExcelFileReader();
            _csvReader = new CsvFileReader();
            _storageService = new ImportStorageService();
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult UploadFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, error = "ไฟล์นี้ไม่มีข้อมูลอยู่เลย" });
            }

            // 1. Check file size (Max 25 MB)
            const long maxFileSize = 25 * 1024 * 1024; // 25 MB
            if (file.Length > maxFileSize)
            {
                return Json(new { success = false, error = "ไฟล์มีขนาดใหญ่เกินไป (สูงสุด 25 MB)" });
            }

            // 2. Validate file extension (Strict check from filename, not just Content-Type)
            string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension != ".xlsx" && extension != ".csv")
            {
                return Json(new { success = false, error = "ไม่รองรับไฟล์ประเภทนี้ กรุณาเลือกไฟล์ .xlsx หรือ .csv" });
            }

            try
            {
                ImportedFile importedFile;

                using (var stream = file.OpenReadStream())
                {
                    if (extension == ".xlsx")
                    {
                        importedFile = _excelReader.ReadWorkbook(stream, file.FileName);
                    }
                    else
                    {
                        importedFile = _csvReader.ReadCsv(stream, file.FileName);
                    }
                }

                // 3. Check if file is completely empty (no rows in any sheet)
                bool isEmpty = true;
                foreach (var sheet in importedFile.Sheets)
                {
                    if (sheet.RowCount > 0)
                    {
                        isEmpty = false;
                        break;
                    }
                }

                if (isEmpty)
                {
                    return Json(new { success = false, error = "ไฟล์นี้ไม่มีข้อมูลอยู่เลย" });
                }

                // 4. Save to temporary storage cache
                _storageService.SaveImport(importedFile);

                // Collect sheet info for the response
                int sheetCount = importedFile.Sheets.Count;
                var sheetNames = new List<string>();
                int totalRows = 0;

                foreach (var sheet in importedFile.Sheets)
                {
                    sheetNames.Add(sheet.SheetName);
                    totalRows += sheet.RowCount;
                }

                return Json(new {
                    success = true,
                    sessionId = importedFile.SessionId,
                    fileName = importedFile.FileName,
                    fileType = importedFile.FileType,
                    sheetCount = sheetCount,
                    sheetNames = sheetNames,
                    totalRows = totalRows
                });
            }
            catch (Exception ex)
            {
                // Inspect for password protection exception or normal corruption
                string errorMessage = "ไม่สามารถเปิดไฟล์นี้ได้ ไฟล์อาจเสียหายหรือถูกล็อกด้วยรหัสผ่าน";
                
                // ClosedXML throws specific exceptions for password-protected files or formats
                if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || 
                    ex.Message.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "ไฟล์นี้มีการตั้งรหัสผ่านป้องกันไว้ กรุณานำรหัสผ่านออกก่อนอัปโหลด";
                }

                return Json(new { success = false, error = errorMessage });
            }
        }
    }
}
