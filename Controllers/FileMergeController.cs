using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using CostFlow.Data;
using CostFlow.Models;
using CostFlow.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CostFlow.Controllers
{
    [Authorize(Roles = "Admin")]
    public class FileMergeController : Controller
    {
        private readonly ImportStorageService _storageService;
        private readonly AppDbContext _context;

        public FileMergeController(AppDbContext context)
        {
            _storageService = new ImportStorageService();
            _context = context;
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

            var poSheet = file.Sheets.FirstOrDefault(s => s.SheetName.Equals("mcsAppvProduct", StringComparison.OrdinalIgnoreCase))
                          ?? file.Sheets.FirstOrDefault();

            if (poSheet == null)
            {
                return Json(new { success = false, error = "ไม่พบชีทข้อมูลในไฟล์นี้" });
            }

            var previewList = new List<object>();
            var distinctRows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            int startRowIndex = poSheet.RawRows.Count > 5 ? 5 : 0;
            for (int r = startRowIndex; r < Math.Min(poSheet.RawRows.Count, startRowIndex + 500); r++)
            {
                var row = poSheet.RawRows[r];
                if (row.Count == 0 || string.IsNullOrEmpty(row[0])) continue;

                string poNum = GetColVal(row, 0).Trim();
                if (string.IsNullOrEmpty(poNum)) continue;

                if (!distinctRows.ContainsKey(poNum))
                {
                    distinctRows[poNum] = row;
                }
            }

            foreach (var kvp in distinctRows)
            {
                string poNum = kvp.Key;
                var row = kvp.Value;
                previewList.Add(new {
                    poNumber = poNum,
                    requestDate = GetColVal(row, 1),
                    approvedDate = GetColVal(row, 2),
                    urgency = GetColVal(row, 4),
                    amount = GetColVal(row, 17),
                    remarks = GetColVal(row, 22),
                    quantity = ExtractQuantity(GetColVal(row, 22))
                });
            }

            return Json(new { success = true, items = previewList, totalCount = distinctRows.Count });
        }

        // --- STEP 2: Save Master File ---
        [HttpPost]
        public IActionResult ConfirmSaveMaster(Guid sessionId, string? reportName)
        {
            try
            {
                var file = _storageService.GetImport(sessionId);
                if (file == null)
                {
                    return Json(new { success = false, error = "เซสชันไฟล์หมดอายุ (เกิน 30 นาที) กรุณาอัปโหลดใหม่" });
                }

                string baseReportName = !string.IsNullOrWhiteSpace(reportName) ? reportName.Trim() : file.FileName;
                
                // Check for duplicate report name and auto-increment
                string finalReportName = baseReportName;
                int counter = 1;
                while (_context.Reports.Any(r => r.ReportName == finalReportName))
                {
                    finalReportName = $"{baseReportName} ({counter})";
                    counter++;
                }

                var poSheet = file.Sheets.FirstOrDefault(s => s.SheetName.Equals("mcsAppvProduct", StringComparison.OrdinalIgnoreCase))
                              ?? file.Sheets.FirstOrDefault();

                if (poSheet == null)
                {
                    return Json(new { success = false, error = "ไม่พบชีทข้อมูลในไฟล์นี้" });
                }

                // Extract distinct PO rows
                var distinctRows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                int startRowIndex = poSheet.RawRows.Count > 5 ? 5 : 0;
                for (int r = startRowIndex; r < poSheet.RawRows.Count; r++)
                {
                    var row = poSheet.RawRows[r];
                    if (row.Count == 0 || string.IsNullOrEmpty(row[0])) continue;

                    string poNum = GetColVal(row, 0).Trim();
                    if (string.IsNullOrEmpty(poNum)) continue;
                    
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
                    CreatedAt = DateTime.Now
                };
                _context.Reports.Add(newReport);

                // Create OrderTrackingMaster records
                foreach (var kvp in distinctRows)
                {
                    string poNum = kvp.Key;
                    var row = kvp.Value;

                    var newOrder = new OrderTrackingMaster
                    {
                        Id = Guid.NewGuid(),
                        ReportId = newReport.Id,
                        PoNumber = poNum,
                        RequestDate = GetColVal(row, 1),
                        ApprovedDate = GetColVal(row, 2),
                        Urgency = GetColVal(row, 4),
                        Amount = GetColVal(row, 17),
                        Remarks = GetColVal(row, 22),
                        RemarksQuantity = ExtractQuantity(GetColVal(row, 22)),
                        Status = "Pending",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    _context.OrderTrackingMasters.Add(newOrder);
                }

                _context.SaveChanges();
                _storageService.DeleteImport(sessionId);

                return Json(new { success = true, insertCount = distinctRows.Count, updateCount = 0, totalCount = distinctRows.Count, fileName = finalReportName });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = "เกิดข้อผิดพลาดในการบันทึกข้อมูล: " + ex.Message + (ex.InnerException != null ? " -> " + ex.InnerException.Message : "") });
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
                var report = _context.Reports.FirstOrDefault(r => r.ReportName == request.ReportName);
                if (report == null)
                    return Json(new { success = false, error = "ไม่พบรายงานหลัก" });

                var duplicates = new List<string>();

                foreach (var fileInfo in request.Files)
                {
                    var importedFile = _storageService.GetImport(fileInfo.SessionId);
                    if (importedFile == null) continue;

                    var existingPlan = _context.WeeklyPlans
                        .FirstOrDefault(wp => wp.ReportId == report.Id && 
                                              wp.FileName == importedFile.FileName && 
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
        public IActionResult ProcessWeeklyPlan([FromBody] WeeklyPlanProcessRequest request)
        {
            if (request == null || request.Files == null || request.Files.Count == 0)
                return Json(new { success = false, error = "ไม่มีข้อมูลไฟล์" });

            try
            {
                var report = _context.Reports.FirstOrDefault(r => r.ReportName == request.ReportName);
                if (report == null)
                    return Json(new { success = false, error = "ไม่พบรายงานหลัก" });

                // Get all orders from this report
                var allOrders = _context.OrderTrackingMasters
                    .Where(o => o.ReportId == report.Id)
                    .ToList();

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

                    // Check for duplicate uploads (overwrite logic)
                    var existingPlan = _context.WeeklyPlans
                        .FirstOrDefault(wp => wp.ReportId == report.Id && 
                                              wp.FileName == importedFile.FileName && 
                                              wp.SheetName == selectedSheet.SheetName);
                    
                    if (existingPlan != null)
                    {
                        debugInfo.Add($"   ⚠️ พบไฟล์ '{importedFile.FileName}' ชีท '{selectedSheet.SheetName}' ซ้ำในระบบ - ทำการลบข้อมูลเก่าเพื่อบันทึกใหม่ (Overwrite)");
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
                        UploadedAt = DateTime.Now
                    };
                    _context.WeeklyPlans.Add(weeklyPlan);

                    int fileMatchedCount = 0;
                    var foundPOs = new List<string>(); // เก็บ PO ที่เจอทุกแถว
                    var matchedUniquePOsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // เก็บ PO ที่จับคู่ได้แบบไม่ซ้ำ

                    // หา column index ของ "ใบขออนุมัติ" หรือ "เลขที่อนุมัติ" จาก header
                    int poColumnIndex = -1;
                    for (int r = 0; r < Math.Min(5, selectedSheet.RawRows.Count); r++)
                    {
                        var headerRow = selectedSheet.RawRows[r];
                        for (int c = 0; c < headerRow.Count; c++)
                        {
                            var cellValue = GetColVal(headerRow, c);
                            // ค้นหาคำว่า "อนุมัติ" ในชื่อคอลัมน์
                            if (cellValue.Contains("อนุมัติ", StringComparison.OrdinalIgnoreCase))
                            {
                                poColumnIndex = c;
                                debugInfo.Add($"   ✅ เจอคอลัมน์ PO: '{cellValue}' ที่ตำแหน่ง {c}");
                                break;
                            }
                        }
                        if (poColumnIndex != -1) break;
                    }

                    // ถ้าไม่เจอ header ให้ fallback เป็นคอลัมน์ 1 (หรือ 2 ถ้าเริ่มนับจาก 0)
                    if (poColumnIndex == -1)
                    {
                        poColumnIndex = 1; // เปลี่ยนจาก 3 เป็น 1
                        debugInfo.Add($"   ⚠️ ไม่เจอ header 'อนุมัติ' ใช้คอลัมน์ {poColumnIndex} แทน");
                    }

                    // Process each row
                    for (int r = 0; r < selectedSheet.RawRows.Count; r++)
                    {
                        var row = selectedSheet.RawRows[r];
                        if (row.Count == 0) continue;

                        // ดึง PO จากคอลัมน์ที่หาเจอ โดยเลือกเฉพาะที่ขึ้นต้นด้วย WO
                        string poNumberInFile = ExtractPoNumberFromColumn(row, poColumnIndex);
                        if (string.IsNullOrEmpty(poNumberInFile)) continue;
                        
                        foundPOs.Add(poNumberInFile); // เก็บไว้ debug

                        // Clean PO number (digits only)
                        string cleanPoInFile = CleanKey(poNumberInFile);

                        // Try to match with existing orders
                        var matchedOrder = allOrders.FirstOrDefault(o =>
                            CleanKey(o.PoNumber) == cleanPoInFile
                        );

                        // Extract data from correct columns based on your file structure
                        // จากข้อมูลจริง:
                        // 0=ลำดับ, 1=เลขที่อนุมัติ(WO), 2=วันที่รับPO, 3=วันที่เปิดใบสั่ง,
                        // 4=เลขที่ใบสั่ง, 5=หน่วยงาน, 6=สาขา, 7=ชื่อใบสั่ง, 8=ประเภทงาน,
                        // 9=จำนวนชิ้น, 10=ราคา, 11=สถานะใบสั่ง, 12=ส่งมอบ, 13+=อื่นๆ
                        string department = GetColVal(row, 5);      // หน่วยงาน (LCD00, LCA00)
                        string orderName = GetColVal(row, 7);       // ชื่อใบสั่ง
                        string orderStatus = GetColVal(row, 11);    // สถานะใบสั่ง (C:ปิดใบสั่ง, O:กำลังดำเนินการ)
                        string deliveryTarget = GetColVal(row, 12); // ส่งมอบ (วันที่)

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
                            Price = GetColVal(row, 10),
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
                        totalCount = uniqueTotalInFile,        // จำนวน PO ที่ไม่ซ้ำ
                        matchedCount = uniqueMatchedInFile,    // จำนวน PO ที่จับคู่ได้ไม่ซ้ำ
                        totalRowsCount = foundPOs.Count,       // จำนวนแถวทั้งหมดใน Excel
                        matchedRowsCount = fileMatchedCount    // จำนวนแถวที่จับคู่ได้
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

                // Reconcile order statuses based on all active WeeklyPlanDetails for this report in the DB
                var allActiveDetails = _context.WeeklyPlanDetails
                    .Where(d => d.WeeklyPlan.ReportId == report.Id)
                    .ToList();

                foreach (var order in allOrders)
                {
                    bool hasMatch = allActiveDetails.Any(d => d.MatchedOrderId == order.Id);
                    order.Status = hasMatch ? "Matched" : "Pending";
                    order.UpdatedAt = DateTime.Now;
                }

                // Update report matched count
                report.MatchedPOs = allOrders.Count(o => o.Status == "Matched");

                _context.SaveChanges();

                return Json(new
                {
                    success = true,
                    totalMatched = report.MatchedPOs,
                    totalRowsProcessed = allActiveDetails.Count,
                    fileResults = fileResults,
                    message = $"จับคู่สำเร็จทั้งหมด {report.MatchedPOs} ใบสั่งผลิต",
                    debug = string.Join("\n", debugInfo) // ส่ง debug info กลับไป
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = "เกิดข้อผิดพลาด: " + ex.Message });
            }
        }

        // --- Helper methods ---
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
            string[] patterns = {
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
            if (DateTime.TryParseExact(val, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d2))
            {
                return d2.ToString("dd/MM/yyyy");
            }
            return val.Trim();
        }
    }
}
