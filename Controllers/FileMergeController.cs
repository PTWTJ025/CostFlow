using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using CostFlow.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Globalization;

namespace CostFlow.Controllers
{
    [Authorize(Roles = "Admin,Dev")]
    public class FileMergeController : Controller
    {
        private readonly ImportStorageService _storageService;
        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public FileMergeController(AppDbContext context, IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _storageService = new ImportStorageService();
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public IActionResult Index()
        {
            return View();
        }

        // --- STEP 1: Preview Master File ---
        [HttpPost]
        public IActionResult PreviewMasterFile(Guid sessionId)
        {
            var file = _storageService.GetImport(sessionId);
            if (file == null)
            {
                return Json(new { success = false, error = "เซสชันไฟล์หมดอายุ (เกิน 30 นาที) กรุณาอัปโหลดใหม่" });
            }

            var poSheet =
                file.Sheets.FirstOrDefault(s =>
                    s.SheetName.Equals("mcsAppvProduct", StringComparison.OrdinalIgnoreCase))
                ?? file.Sheets.FirstOrDefault();

            if (poSheet == null)
            {
                return Json(new { success = false, error = "ไม่พบชีทข้อมูลในไฟล์นี้" });
            }

            var previewList = new List<object>();
            var distinctRows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            for (int r = 0; r < poSheet.RawRows.Count; r++)
            {
                var row = poSheet.RawRows[r];
                if (row.Count == 0 || string.IsNullOrEmpty(row[0])) continue;

                string poNum = GetColVal(row, 0).Trim();
                if (string.IsNullOrEmpty(poNum) || IsHeaderRow(poNum)) continue;

                if (!distinctRows.ContainsKey(poNum))
                {
                    distinctRows[poNum] = row;
                }
            }

            foreach (var kvp in distinctRows)
            {
                string poNum = kvp.Key;
                var row = kvp.Value;
                previewList.Add(new
                {
                    poNumber = poNum,
                    requestDate = GetColVal(row, 1),
                    approvedDate = GetColVal(row, 2),
                    urgency = GetColVal(row, 4),
                    amount = CleanAmount(GetColVal(row, 17)),
                    remarks = GetColVal(row, 22),
                    quantity = ExtractQuantity(GetColVal(row, 22))
                });
            }

            return Json(new { success = true, items = previewList, totalCount = distinctRows.Count });
        }

        // --- STEP 2: Save Master File ---
        [HttpPost]
        public async Task<IActionResult> ConfirmSaveMaster(Guid sessionId)
        {
            try
            {
                var file = _storageService.GetImport(sessionId);
                if (file == null)
                {
                    return Json(new { success = false, error = "เซสชันไฟล์หมดอายุ (เกิน 30 นาที) กรุณาอัปโหลดใหม่" });
                }

                // สร้างชื่อรายงานจากวันที่ปัจจุบัน (ใช้ format ตัวเลขเพื่อความปลอดภัย)
                string baseReportName = $"รายงานสั่งผลิต_{DateTime.Now:dd_MM_yyyy_HH_mm}";

                // Check for duplicate report name and auto-increment
                string finalReportName = baseReportName;
                int counter = 1;
                while (_context.Reports.Any(r => r.ReportName == finalReportName))
                {
                    finalReportName = $"{baseReportName}_v{counter}";
                    counter++;
                }

                var poSheet = file.Sheets.FirstOrDefault(s =>
                                  s.SheetName.Equals("mcsAppvProduct", StringComparison.OrdinalIgnoreCase))
                              ?? file.Sheets.FirstOrDefault();

                if (poSheet == null)
                {
                    return Json(new { success = false, error = "ไม่พบชีทข้อมูลในไฟล์นี้" });
                }

                // Extract distinct PO rows
                var distinctRows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                for (int r = 0; r < poSheet.RawRows.Count; r++)
                {
                    var row = poSheet.RawRows[r];
                    if (row.Count == 0 || string.IsNullOrEmpty(row[0])) continue;

                    string poNum = GetColVal(row, 0).Trim();
                    if (string.IsNullOrEmpty(poNum) || IsHeaderRow(poNum)) continue;

                    if (!distinctRows.ContainsKey(poNum))
                    {
                        distinctRows[poNum] = row;
                    }
                }

                // Create new Report
                var newReport = new Report
                {
                    Id = Guid.NewGuid(),
                    ReportName = finalReportName,
                    OriginalFileName = file.FileName,
                    TotalPOs = distinctRows.Count,
                    MatchedPOs = 0,
                    CreatedAt = DateTime.Now,
                    CreatedBy = User.Identity?.Name ?? "System",
                    CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                };
                _context.Reports.Add(newReport);

                // Get existing PO numbers from database to check for duplicates
                var existingPoNumbers = _context.OrderTrackingMasters
                    .Select(o => o.PoNumber)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Create OrderTrackingMaster records (skip duplicates)
                int insertCount = 0;
                int skippedCount = 0;
                var skippedPOs = new List<string>();

                foreach (var kvp in distinctRows)
                {
                    string poNum = kvp.Key;
                    var row = kvp.Value;

                    // Check if PO already exists
                    if (existingPoNumbers.Contains(poNum))
                    {
                        skippedCount++;
                        skippedPOs.Add(poNum);
                        continue; // Skip duplicate PO
                    }

                    var newOrder = new OrderTrackingMaster
                    {
                        Id = Guid.NewGuid(),
                        ReportId = newReport.Id,
                        PoNumber = poNum,
                        RequestDate = GetColVal(row, 1),
                        ApprovedDate = GetColVal(row, 2),
                        Urgency = GetColVal(row, 4),
                        Amount = CleanAmount(GetColVal(row, 17)),
                        Remarks = GetColVal(row, 22),
                        RemarksQuantity = ExtractQuantity(GetColVal(row, 22)),
                        Status = "Pending",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    _context.OrderTrackingMasters.Add(newOrder);
                    insertCount++;
                }

                // Update report total POs to reflect actual inserted count
                newReport.TotalPOs = insertCount;

                _context.SaveChanges();

                await SyncWeeklyPlansToGoogleSheetsAsync(newReport.Id);

                _storageService.DeleteImport(sessionId);

                var message = insertCount > 0
                    ? $"นำเข้าสำเร็จ {insertCount} รายการ"
                    : "ไม่มีรายการใหม่ถูกนำเข้า";

                if (skippedCount > 0)
                {
                    message += $" (ข้าม {skippedCount} รายการที่มีอยู่แล้ว)";
                }

                return Json(new
                {
                    success = true,
                    insertCount = insertCount,
                    skippedCount = skippedCount,
                    totalCount = distinctRows.Count,
                    fileName = finalReportName,
                    message = message,
                    skippedPOs = skippedPOs.Take(10).ToList() // Show first 10 skipped POs for debugging
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    error = "เกิดข้อผิดพลาดในการบันทึกข้อมูล: " + ex.Message +
                            (ex.InnerException != null ? " -> " + ex.InnerException.Message : "")
                });
            }
        }

        // --- STEP 3: Upload Multiple Weekly Plan Files (สูงสุด 10 ไฟล์) ---
        [HttpPost]
        public async Task<IActionResult> UploadWeeklyPlanFiles(List<Microsoft.AspNetCore.Http.IFormFile> files)
        {
            if (files == null || files.Count == 0)
                return Json(new { success = false, error = "กรุณาเลือกไฟล์อย่างน้อย 1 ไฟล์" });

            if (files.Count > 10)
                return Json(new { success = false, error = "อัปโหลดได้สูงสุด 10 ไฟล์เท่านั้น" });

            try
            {
                var uploadedFiles = new List<object>();
                var excelReader = new ExcelFileReader();
                var csvReader = new CsvFileReader();

                foreach (var file in files)
                {
                    if (file.Length == 0) continue;

                    // Validate file type
                    var extension = Path.GetExtension(file.FileName).ToLower();
                    if (extension != ".xlsx" && extension != ".xls" && extension != ".csv")
                        continue;

                    // Parse file
                    ImportedFile importedFile;
                    using (var stream = file.OpenReadStream())
                    {
                        importedFile = await Task.Run(() =>
                        {
                            if (extension == ".csv")
                                return csvReader.ReadCsv(stream, file.FileName);
                            else
                                return excelReader.ReadWorkbook(stream, file.FileName);
                        });
                    }

                    // Save to temp storage
                    _storageService.SaveImport(importedFile);

                    // Get sheet names
                    var sheetNames = importedFile.Sheets.Select(s => s.SheetName).ToList();

                    uploadedFiles.Add(new
                    {
                        sessionId = importedFile.SessionId,
                        fileName = file.FileName,
                        sheets = sheetNames
                    });
                }

                return Json(new { success = true, files = uploadedFiles });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = "เกิดข้อผิดพลาดในการอัปโหลด: " + ex.Message });
            }
        }

        [HttpPost]
        public IActionResult CheckDuplicateWeeklyPlans([FromBody] WeeklyPlanProcessRequest request)
        {
            if (request == null || request.Files == null || request.Files.Count == 0)
                return Json(new { success = true, duplicates = new List<string>() });

            try
            {
                var duplicates = new List<string>();

                foreach (var fileInfo in request.Files)
                {
                    var importedFile = _storageService.GetImport(fileInfo.SessionId);
                    if (importedFile == null) continue;

                    // Check if this filename+sheet combination already exists (across all reports)
                    var existingPlan = _context.WeeklyPlans
                        .FirstOrDefault(wp => wp.FileName == importedFile.FileName &&
                                              wp.SheetName == fileInfo.SelectedSheet);

                    if (existingPlan != null)
                    {
                        duplicates.Add($"{importedFile.FileName} (ชีท: {fileInfo.SelectedSheet})");
                    }
                }

                return Json(new { success = true, duplicates = duplicates });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = "เกิดข้อผิดพลาดในการตรวจสอบ: " + ex.Message });
            }
        }

        // --- STEP 4: Process Weekly Plan (รับ sheet ที่เลือกมาแล้ว) ---
        [HttpPost]
        public async Task<IActionResult> ProcessWeeklyPlan([FromBody] WeeklyPlanProcessRequest request)
        {
            if (request == null || request.Files == null || request.Files.Count == 0)
                return Json(new { success = false, error = "ไม่มีข้อมูลไฟล์" });

            try
            {
                // Get the first (most recent) report as the primary report for linking WeeklyPlans
                // This is needed because WeeklyPlan table has ReportId as required FK
                var report = _context.Reports.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
                if (report == null)
                    return Json(
                        new { success = false, error = "ไม่พบรายงานสั่งผลิตในระบบ กรุณานำเข้าไฟล์สั่งผลิตก่อน" });

                // Get ALL orders from the system (not limited to specific report)
                var allOrders = _context.OrderTrackingMasters.ToList();
                var ordersByPo = allOrders
                    .Where(o => !string.IsNullOrWhiteSpace(CleanKey(o.PoNumber)))
                    .GroupBy(o => CleanKey(o.PoNumber), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                int totalMatched = 0;
                var fileResults = new List<object>();
                var debugInfo = new List<string>(); // เพิ่ม debug info

                foreach (var fileInfo in request.Files)
                {
                    var importedFile = _storageService.GetImport(fileInfo.SessionId);
                    if (importedFile == null)
                    {
                        debugInfo.Add($"❌ ไม่พบไฟล์ในระบบสำหรับ sessionId: {fileInfo.SessionId}");
                        continue;
                    }

                    // Debug: แสดงชีทที่มีในไฟล์
                    var availableSheets = string.Join(", ", importedFile.Sheets.Select(s => $"'{s.SheetName}'"));
                    debugInfo.Add($"📄 ไฟล์: {importedFile.FileName}");
                    debugInfo.Add($"   - ชีทที่มีในไฟล์: {availableSheets}");
                    debugInfo.Add($"   - ชีทที่เลือก: '{fileInfo.SelectedSheet}'");

                    var selectedSheet = importedFile.Sheets.FirstOrDefault(s => s.SheetName == fileInfo.SelectedSheet);
                    if (selectedSheet == null)
                    {
                        debugInfo.Add($"   ❌ ไม่พบชีท '{fileInfo.SelectedSheet}' ในไฟล์");
                        continue;
                    }

                    debugInfo.Add($"   ✅ พบชีท '{selectedSheet.SheetName}' - มี {selectedSheet.RawRows.Count} แถว");

                    // Read the header once per sheet.  All fields below use this map, so inserting
                    // or moving Excel columns cannot silently shift data into the wrong field.
                    var columns = FindWeeklyPlanColumns(selectedSheet.RawRows);
                    var missingColumns = GetMissingRequiredWeeklyPlanColumns(columns);
                    if (missingColumns.Count > 0)
                    {
                        debugInfo.Add($"   ❌ ไม่พบคอลัมน์ที่จำเป็น: {string.Join(", ", missingColumns)}");
                        fileResults.Add(new
                        {
                            fileName = importedFile.FileName,
                            sheetName = selectedSheet.SheetName,
                            error = $"ไม่พบคอลัมน์ที่จำเป็น: {string.Join(", ", missingColumns)}"
                        });
                        continue;
                    }

                    debugInfo.Add($"   ✅ อ่านหัวตารางแถว {columns.HeaderRowIndex + 1}: " +
                                  $"PO={columns.PoNumber}, หน่วยงาน={columns.Department}, ชื่อใบสั่ง={columns.OrderName}, " +
                                  $"ราคา={columns.Price}, สถานะ={columns.OrderStatus}, ส่งมอบ={columns.DeliveryTarget}");

                    // Check for duplicate uploads (overwrite logic)
                    var existingPlan = _context.WeeklyPlans
                        .FirstOrDefault(wp => wp.ReportId == report.Id &&
                                              wp.FileName == importedFile.FileName &&
                                              wp.SheetName == selectedSheet.SheetName);

                    if (existingPlan != null)
                    {
                        debugInfo.Add(
                            $"   ⚠️ พบไฟล์ '{importedFile.FileName}' ชีท '{selectedSheet.SheetName}' ซ้ำในระบบ - ทำการลบข้อมูลเก่าเพื่อบันทึกใหม่ (Overwrite)");
                        // Remove old details
                        var oldDetails = _context.WeeklyPlanDetails.Where(d => d.WeeklyPlanId == existingPlan.Id);
                        _context.WeeklyPlanDetails.RemoveRange(oldDetails);
                        // Remove old plan
                        _context.WeeklyPlans.Remove(existingPlan);
                        _context.SaveChanges();
                    }

                    // Create WeeklyPlan record
                    var weeklyPlan = new WeeklyPlan
                    {
                        Id = Guid.NewGuid(),
                        ReportId = report.Id,
                        FileName = importedFile.FileName,
                        SheetName = selectedSheet.SheetName,
                        TotalRecords = selectedSheet.RawRows.Count,
                        MatchedCount = 0,
                        UploadedAt = DateTime.Now,
                        UploadedBy = User.Identity?.Name ?? "System"
                    };
                    _context.WeeklyPlans.Add(weeklyPlan);

                    int fileMatchedCount = 0;
                    var foundPOs = new List<string>(); // เก็บ PO ที่เจอทุกแถว
                    var matchedUniquePOsInFile =
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase); // เก็บ PO ที่จับคู่ได้แบบไม่ซ้ำ

                    // Process each row
                    for (int r = columns.HeaderRowIndex + 1; r < selectedSheet.RawRows.Count; r++)
                    {
                        var row = selectedSheet.RawRows[r];
                        if (row.Count == 0) continue;

                        // ดึง PO จากคอลัมน์ที่หาเจอ โดยเลือกเฉพาะที่ขึ้นต้นด้วย WO
                        string poNumberInFile = ExtractPoNumberFromColumn(row, columns.PoNumber);
                        if (string.IsNullOrEmpty(poNumberInFile)) continue;

                        foundPOs.Add(poNumberInFile); // เก็บไว้ debug

                        // Clean PO number (digits only)
                        string cleanPoInFile = CleanKey(poNumberInFile);

                        // Try to match with existing orders
                        ordersByPo.TryGetValue(cleanPoInFile, out var matchedOrder);

                        string department = GetColVal(row, columns.Department);
                        string orderName = GetColVal(row, columns.OrderName);
                        string orderStatus = GetColVal(row, columns.OrderStatus);
                        string deliveryTarget = GetColVal(row, columns.DeliveryTarget);

                        // Create WeeklyPlanDetail (บันทึกประวัติทุกแถวตามจริง ไม่ตัดทิ้ง เพื่อเวลาคลิกตรวจสอบจะได้เห็นครบทุกงวด/สถานะ)
                        var detail = new WeeklyPlanDetail
                        {
                            Id = Guid.NewGuid(),
                            WeeklyPlanId = weeklyPlan.Id,
                            PoNumberInFile = poNumberInFile,
                            Department = department,
                            OrderName = orderName,
                            OrderStatus = orderStatus,
                            DeliveryTarget = FormatDeliveryTargetDate(deliveryTarget) ?? string.Empty,
                            Price = GetColVal(row, columns.Price),
                            RowIndex = r,
                            IsMatched = matchedOrder != null,
                            MatchedOrderId = matchedOrder?.Id
                        };
                        _context.WeeklyPlanDetails.Add(detail);

                        // Count matching order regardless of its previous status so re-uploading/overwriting reports accurate count
                        if (matchedOrder != null)
                        {
                            if (matchedOrder.Status == "Pending")
                            {
                                matchedOrder.Status = "Matched";
                                matchedOrder.UpdatedAt = DateTime.Now;
                            }

                            fileMatchedCount++;
                            totalMatched++;
                            matchedUniquePOsInFile.Add(cleanPoInFile);
                        }
                    }

                    int uniqueTotalInFile = foundPOs.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    int uniqueMatchedInFile = matchedUniquePOsInFile.Count;

                    weeklyPlan.MatchedCount = uniqueMatchedInFile; // เก็บจำนวน PO ที่ไม่ซ้ำ
                    fileResults.Add(new
                    {
                        fileName = importedFile.FileName,
                        sheetName = selectedSheet.SheetName,
                        totalCount = uniqueTotalInFile, // จำนวน PO ที่ไม่ซ้ำ
                        matchedCount = uniqueMatchedInFile, // จำนวน PO ที่จับคู่ได้ไม่ซ้ำ
                        totalRowsCount = foundPOs.Count, // จำนวนแถวทั้งหมดใน Excel
                        matchedRowsCount = fileMatchedCount // จำนวนแถวที่จับคู่ได้
                    });

                    // Debug: แสดง PO ที่เจอ
                    debugInfo.Add($"   - เจอ PO ทั้งหมด: {foundPOs.Count} รายการ");
                    if (foundPOs.Count > 0)
                    {
                        debugInfo.Add($"   - PO 5 ตัวแรก: {string.Join(", ", foundPOs.Take(5))}");
                    }

                    debugInfo.Add($"   - จับคู่ได้: {fileMatchedCount} รายการ");

                    // Delete temp file
                    _storageService.DeleteImport(fileInfo.SessionId);
                }

                // Save changes to commit the new plan and details first
                _context.SaveChanges();

                // Reconcile order statuses based on ALL active WeeklyPlanDetails in the system
                var allActiveDetails = _context.WeeklyPlanDetails.ToList();

                foreach (var order in allOrders)
                {
                    bool hasMatch = allActiveDetails.Any(d => d.MatchedOrderId == order.Id);
                    order.Status = hasMatch ? "Matched" : "Pending";
                    order.UpdatedAt = DateTime.Now;
                }

                // Update ALL reports matched count (not just one report)
                var allReports = _context.Reports.ToList();
                foreach (var rpt in allReports)
                {
                    var ordersInThisReport = allOrders.Where(o => o.ReportId == rpt.Id).ToList();
                    rpt.MatchedPOs = ordersInThisReport.Count(o => o.Status == "Matched");
                }

                _context.SaveChanges();

                await SyncWeeklyPlansToGoogleSheetsAsync();

                return Json(new
                {
                    success = true,
                    totalMatched = allOrders.Count(o => o.Status == "Matched"),
                    totalRowsProcessed = allActiveDetails.Count,
                    fileResults = fileResults,
                    message = $"จับคู่สำเร็จทั้งหมด {allOrders.Count(o => o.Status == "Matched")} ใบสั่งผลิต",
                    debug = string.Join("\n", debugInfo) // ส่ง debug info กลับไป
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = "เกิดข้อผิดพลาด: " + ex.Message });
            }
        }

        // --- Helper methods ---
        private static WeeklyPlanColumnMap FindWeeklyPlanColumns(List<List<string>> rows)
        {
            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(WeeklyPlanColumnMap.PoNumber)] = new[] { "เลขที่อนุมัติ", "ใบขออนุมัติ", "PO Number", "PO" },
                [nameof(WeeklyPlanColumnMap.Department)] = new[] { "หน่วยงาน", "แผนก" },
                [nameof(WeeklyPlanColumnMap.OrderName)] = new[] { "ชื่อใบสั่ง", "รายละเอียดใบสั่ง" },
                [nameof(WeeklyPlanColumnMap.Price)] = new[] { "ราคาประมาณการมี +-5%", "ราคาประมาณการมี +/-5%", "ราคาประมาณการ", "ราคา", "ราคาประมาณการณ์ +-5%", "ราคาประมาณการณ์" },
                [nameof(WeeklyPlanColumnMap.OrderStatus)] = new[] { "สถานะใบสั่ง", "สถานะ" },
                [nameof(WeeklyPlanColumnMap.DeliveryTarget)] = new[] { "ส่งมอบ", "กำหนดส่งมอบ" }
            };

            var map = new WeeklyPlanColumnMap();
            for (int rowIndex = 0; rowIndex < Math.Min(15, rows.Count); rowIndex++)
            {
                var row = rows[rowIndex];
                var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int columnIndex = 0; columnIndex < row.Count; columnIndex++)
                {
                    var normalizedHeader = NormalizeWeeklyPlanHeader(row[columnIndex]);
                    if (string.IsNullOrEmpty(normalizedHeader)) continue;

                    foreach (var field in aliases)
                    {
                        if (!found.ContainsKey(field.Key) &&
                            field.Value.Any(alias => normalizedHeader == NormalizeWeeklyPlanHeader(alias)))
                        {
                            found[field.Key] = columnIndex;
                        }
                    }
                }

                // A valid header row must at least identify the PO column; the remaining required
                // fields are validated by the caller to produce a useful import error.
                if (!found.TryGetValue(nameof(WeeklyPlanColumnMap.PoNumber), out var poNumber)) continue;

                map.HeaderRowIndex = rowIndex;
                map.PoNumber = poNumber;
                map.Department = found.GetValueOrDefault(nameof(WeeklyPlanColumnMap.Department), -1);
                map.OrderName = found.GetValueOrDefault(nameof(WeeklyPlanColumnMap.OrderName), -1);
                map.Price = found.GetValueOrDefault(nameof(WeeklyPlanColumnMap.Price), -1);
                map.OrderStatus = found.GetValueOrDefault(nameof(WeeklyPlanColumnMap.OrderStatus), -1);
                map.DeliveryTarget = found.GetValueOrDefault(nameof(WeeklyPlanColumnMap.DeliveryTarget), -1);
                return map;
            }

            return map;
        }

        private static List<string> GetMissingRequiredWeeklyPlanColumns(WeeklyPlanColumnMap columns)
        {
            var missing = new List<string>();
            if (columns.PoNumber < 0) missing.Add("เลขที่อนุมัติ");
            if (columns.Department < 0) missing.Add("หน่วยงาน");
            if (columns.OrderName < 0) missing.Add("ชื่อใบสั่ง");
            if (columns.Price < 0) missing.Add("ราคาประมาณการมี +-5%");
            if (columns.OrderStatus < 0) missing.Add("สถานะใบสั่ง");
            if (columns.DeliveryTarget < 0) missing.Add("ส่งมอบ");
            return missing;
        }

        private static string NormalizeWeeklyPlanHeader(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var normalized = value.Replace("\u200b", "").Replace("\u00a0", "").Replace("\uFEFF", "");
            return Regex.Replace(normalized, @"[\s\-_()/\\.]", string.Empty).ToUpperInvariant();
        }

        private sealed class WeeklyPlanColumnMap
        {
            public int HeaderRowIndex { get; set; } = -1;
            public int PoNumber { get; set; } = -1;
            public int Department { get; set; } = -1;
            public int OrderName { get; set; } = -1;
            public int Price { get; set; } = -1;
            public int OrderStatus { get; set; } = -1;
            public int DeliveryTarget { get; set; } = -1;
        }

        private string ExtractPoNumberFromColumn(List<string> row, int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= row.Count)
                return string.Empty;

            var val = GetColVal(row, columnIndex);
            if (string.IsNullOrWhiteSpace(val)) return string.Empty;

            // ลบอักขระที่มองไม่เห็น เช่น Zero-Width Space, NBSP ที่ Excel แอบแทรกมา
            val = val.Replace("\u200b", "").Replace("\u00a0", "").Replace("\uFEFF", "").Trim();

            // Match WO pattern: WO26010017 หรือ WO-26010017
            // ยืดหยุ่นรับตัวเลข 6-10 หลักเพื่อรองรับ WO ทุกรูปแบบที่อาจมีในไฟล์จริง
            var match = Regex.Match(val, @"^(WO-?\d{6,10})$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }


        private string ExtractPoNumberFromRow(List<string> row)
        {
            // Fallback: ค้นหาใน 10 คอลัมน์แรก โดยหา WO pattern
            for (int i = 0; i < Math.Min(10, row.Count); i++)
            {
                var val = GetColVal(row, i);
                if (string.IsNullOrWhiteSpace(val)) continue;

                // Match เฉพาะ WO pattern
                if (Regex.IsMatch(val, @"^WO-?\d{8}$", RegexOptions.IgnoreCase))
                {
                    return val;
                }
            }

            return string.Empty;
        }

        private int GetLastNonEmptyColumnIndex(List<string> row)
        {
            for (int i = row.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(row[i]))
                    return i;
            }

            return row.Count > 0 ? row.Count - 1 : 0;
        }

        private string GetColVal(List<string> row, int index)
        {
            return index < row.Count ? (row[index]?.Trim() ?? string.Empty) : string.Empty;
        }

        private string CleanKey(string val)
        {
            if (string.IsNullOrEmpty(val)) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (char c in val)
            {
                if (char.IsDigit(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private string ExtractQuantity(string remarks)
        {
            if (string.IsNullOrEmpty(remarks)) return "-";
            remarks = remarks.Replace("\u200b", "").Trim();
            string[] patterns =
            {
                @"(?:จำนวน|จํานวน|จำนวนชิ้น|จํานวนชิ้น|จำนวน\s*ชิ้น|จำนวณ|จนวน|จํนวน|จำนวน)\s*[:=\-\s]*\s*([0-9]+)",
                @"([0-9]+)\s*(?:ชิ้น|อัน|ตัว|เครื่อง)"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(remarks, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value;
                }
            }

            return "-";
        }

        private string? FormatDeliveryTargetDate(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "";
            if (DateTime.TryParse(val, out var d)) return d.ToString("dd/MM/yyyy");
            string[] formats = { "d/M/yyyy", "d/M/yy", "dd/MM/yyyy", "yyyy-MM-dd", "d/M/yyyy H:mm:ss" };
            if (DateTime.TryParseExact(val, formats, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d2))
            {
                return d2.ToString("dd/MM/yyyy");
            }

            return val.Trim();
        }

        private static bool IsHeaderRow(string poNum)
        {
            if (string.IsNullOrWhiteSpace(poNum)) return true;
            poNum = poNum.Trim();

            // Headers/Titles often contain spaces (e.g. "ขออนุมัติ สั่งผลิตจากต้นสังกัด", "รหัส Profit : IFA")
            // whereas PO Numbers are single codes (e.g. "WO26010001", "WO26050273")
            if (poNum.Contains(" ") || poNum.Contains("\t")) return true;

            if (poNum.Equals("ใบขออนุมัติ", StringComparison.OrdinalIgnoreCase) ||
                poNum.Equals("เลขที่อนุมัติ", StringComparison.OrdinalIgnoreCase) ||
                poNum.Equals("PO Number", StringComparison.OrdinalIgnoreCase) ||
                poNum.Equals("PO", StringComparison.OrdinalIgnoreCase) ||
                poNum.Equals("ลำดับ", StringComparison.OrdinalIgnoreCase) ||
                poNum.Equals("NO", StringComparison.OrdinalIgnoreCase) ||
                poNum.Equals("NO.", StringComparison.OrdinalIgnoreCase) ||
                poNum.StartsWith("ขออนุมัติ", StringComparison.OrdinalIgnoreCase) ||
                poNum.StartsWith("รหัส", StringComparison.OrdinalIgnoreCase) ||
                poNum.StartsWith("[", StringComparison.OrdinalIgnoreCase) ||
                poNum.StartsWith("รายงาน", StringComparison.OrdinalIgnoreCase) ||
                poNum.StartsWith("วันที่", StringComparison.OrdinalIgnoreCase) ||
                poNum.StartsWith("ประเภท", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string CleanAmount(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "0";
            val = val.Replace(",", "").Replace("฿", "").Trim();
            if (decimal.TryParse(val, out var d)) return d.ToString("0.00");
            return val;
        }

        private async Task SyncWeeklyPlansToGoogleSheetsAsync(Guid? targetReportId = null)
        {
            try
            {
                string? appScriptUrl = _configuration["GoogleSheets:MonthlyCostAppScriptUrl"];
                if (string.IsNullOrWhiteSpace(appScriptUrl) || appScriptUrl.Contains("_placeholder"))
                {
                    appScriptUrl = _configuration["GoogleSheets:ArchiveAppScriptUrl"];
                }

                if (!string.IsNullOrWhiteSpace(appScriptUrl) && !appScriptUrl.Contains("_placeholder"))
                {
                    Guid activeReportId = targetReportId ?? await _context.Reports
                        .OrderByDescending(r => r.CreatedAt)
                        .Select(r => r.Id)
                        .FirstOrDefaultAsync();

                    if (activeReportId == Guid.Empty) return;

                    var allOrders = await _context.OrderTrackingMasters
                        .Where(o => o.ReportId == activeReportId)
                        .Include(o => o.Report)
                        .Include(o => o.MatchedInWeeklyPlans)
                        .ThenInclude(m => m.WeeklyPlan)
                        .ToListAsync();

                    var formattedPlanItems = new List<(string MonthYear, object?[] Row)>();

                    foreach (var otm in allOrders)
                    {
                        var latestPlan = otm.MatchedInWeeklyPlans
                            .OrderByDescending(w => w.WeeklyPlan?.UploadedAt)
                            .FirstOrDefault();

                        string poNo = otm.PoNumber ?? "-";
                        string orderName = !string.IsNullOrWhiteSpace(latestPlan?.OrderName)
                            ? latestPlan.OrderName
                            : (!string.IsNullOrWhiteSpace(otm.Remarks) ? otm.Remarks.Trim() : "ไม่ระบุ");
                        string dept = !string.IsNullOrWhiteSpace(latestPlan?.Department)
                            ? latestPlan.Department
                            : (!string.IsNullOrWhiteSpace(otm.Urgency) ? otm.Urgency : "-");
                        string deliveryTarget = !string.IsNullOrWhiteSpace(latestPlan?.DeliveryTarget)
                            ? latestPlan.DeliveryTarget
                            : "-";
                        string approvedDateDisplay = otm.ApprovedDate ?? "-";

                        string monthKey = GetMonthYearFromDateStr(approvedDateDisplay, poNo)
                                          ?? latestPlan?.WeeklyPlan?.UploadedAt.ToLocalTime().ToString("yyyy-MM")
                                          ?? otm.CreatedAt.ToLocalTime().ToString("yyyy-MM");

                        string uploadDate = latestPlan?.WeeklyPlan?.UploadedAt.ToLocalTime().ToString("dd/MM/yyyy")
                                            ?? otm.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy");
                        string fileName = latestPlan?.WeeklyPlan?.FileName ?? otm.Report?.ReportName ?? "-";

                        string rawPlanStatus = latestPlan?.OrderStatus ?? otm.Status ?? "Pending";
                        string planStatusThai = rawPlanStatus switch
                        {
                            "Pending" => "รอดำเนินการ",
                            "Matched" => "จับคู่สำเร็จ",
                            "Completed" => "เสร็จสมบูรณ์",
                            "Approved" => "อนุมัติแล้ว",
                            "In Progress" => "กำลังดำเนินการ",
                            "-" => "รอดำเนินการ",
                            _ => rawPlanStatus
                        };

                        string matchResult = latestPlan != null || otm.Status == "Matched"
                            ? "จับคู่สำเร็จ"
                            : "ยังไม่ได้จับคู่";

                        formattedPlanItems.Add((monthKey, new object?[]
                        {
                            uploadDate, // A วันที่อัปโหลดจริง
                            approvedDateDisplay, // B วันที่อนุมัติ
                            fileName, // C ชื่อไฟล์
                            poNo, // D เลข PO
                            orderName, // E ชื่อสินค้า
                            dept, // F แผนก
                            deliveryTarget, // G กำหนดส่งมอบ
                            planStatusThai, // H สถานะในไฟล์แผน (ภาษาไทย)
                            matchResult // I ผลการจับคู่ (ภาษาไทย)
                        }));
                    }

                    var planGroups = formattedPlanItems
                        .GroupBy(item => item.MonthYear)
                        .OrderBy(g => g.Key)
                        .Select(g => new
                        {
                            MonthYear = g.Key,
                            Rows = g.Select(x => x.Row).ToList()
                        }).ToList();

                    var sheetPayload = new
                    {
                        SheetName_Plans = "แผนผลิตประจำสัปดาห์",
                        PlanGroups = planGroups
                    };

                    var client = _httpClientFactory.CreateClient("GoogleAppsScript");
                    client.Timeout = TimeSpan.FromSeconds(30);

                    var jsonString = System.Text.Json.JsonSerializer.Serialize(sheetPayload);
                    var content =
                        new System.Net.Http.StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(appScriptUrl, content);
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Successfully synced WeeklyPlan_Matching to Google Sheets after FileMerge.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error syncing WeeklyPlan_Matching to Google Sheets: {ex.Message}");
            }
        }

        private static string? GetMonthYearFromDateStr(string? dateStr, string? poNumber = null)
        {
            if (!string.IsNullOrWhiteSpace(dateStr) && dateStr != "-")
            {
                var clean = dateStr.Trim();

                if (DateTime.TryParse(clean, new CultureInfo("th-TH"), DateTimeStyles.None, out var dtThai))
                {
                    int year = dtThai.Year > 2500 ? dtThai.Year - 543 : dtThai.Year;
                    return $"{year:0000}-{dtThai.Month:02}";
                }

                if (DateTime.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtInv))
                {
                    int year = dtInv.Year > 2500 ? dtInv.Year - 543 : dtInv.Year;
                    return $"{year:0000}-{dtInv.Month:02}";
                }
            }

            if (!string.IsNullOrWhiteSpace(poNumber))
            {
                var cleanPo = poNumber.Trim().ToUpper();
                if (cleanPo.StartsWith("WO") && cleanPo.Length >= 6)
                {
                    var yearStr = cleanPo.Substring(2, 2);
                    var monthStr = cleanPo.Substring(4, 2);
                    if (int.TryParse(yearStr, out int y2) && int.TryParse(monthStr, out int m) && m >= 1 && m <= 12)
                    {
                        int fullYear = y2 > 50 ? (y2 + 2500 - 543) : (y2 + 2000);
                        return $"{fullYear:0000}-{m:02}";
                    }
                }
            }

            return null;
        }
    }
}
