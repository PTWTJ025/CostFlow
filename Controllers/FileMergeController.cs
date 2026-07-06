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

namespace CostFlow.Controllers
{
    [Authorize]
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

        // Action to process the dual uploaded files and show the comparison table
        public IActionResult ProcessMerge(Guid sessionIdA, Guid sessionIdB)
        {
            var fileA = _storageService.GetImport(sessionIdA);
            var fileB = _storageService.GetImport(sessionIdB);

            if (fileA == null || fileB == null)
            {
                TempData["ErrorMessage"] = "เซสชันไฟล์อัปโหลดหมดอายุ (เกิน 30 นาที) กรุณาอัปโหลดไฟล์ใหม่อีกครั้ง";
                return RedirectToAction("Index");
            }

            // ===== STEP 1: Build lookup dictionary from File A (Part PO) =====
            var poSheet = fileA.Sheets.FirstOrDefault(s => s.SheetName.Equals("mcsAppvProduct", StringComparison.OrdinalIgnoreCase))
                          ?? fileA.Sheets.FirstOrDefault();

            var poLookup = new Dictionary<string, TempPartPo>(); // cleaned WO key -> row from File A
            if (poSheet != null)
            {
                int startRowIndex = poSheet.RawRows.Count > 5 ? 5 : 0;
                for (int r = startRowIndex; r < poSheet.RawRows.Count; r++)
                {
                    var row = poSheet.RawRows[r];
                    if (row.Count == 0 || string.IsNullOrEmpty(row[0])) continue;

                    string poNum = GetColVal(row, 0);
                    string key = CleanKey(poNum);
                    if (string.IsNullOrEmpty(key)) continue;

                    poLookup[key] = new TempPartPo
                    {
                        PoNumber = poNum,
                        RequestDate = GetColVal(row, 1),
                        ApprovedDate = GetColVal(row, 2),
                        Urgency = GetColVal(row, 4),
                        Amount = GetColVal(row, 17),
                        Remarks = GetColVal(row, 22),
                        RemarksQuantity = ExtractQuantity(GetColVal(row, 22))
                    };
                }
            }

            // ===== STEP 2: Find the main sheet of File B (รวมงานผลิต - ราคาLCA00-LCD00) =====
            var planSheet = fileB.Sheets.FirstOrDefault(s =>
                s.SheetName.Contains("รวมงานผลิต") && s.SheetName.Contains("ราคา"))
                ?? fileB.Sheets.FirstOrDefault();

            var mergedRows = new List<MergeResult>();

            if (planSheet != null)
            {
                // Dynamically find header row and column indices
                int headerRowIndex = -1;
                int colApproveNo = -1;  // เลขที่ (WO number) — Column B
                int colDelivery = -1;   // ส่งมอบ — Column M

                for (int r = 0; r < Math.Min(planSheet.RawRows.Count, 15); r++)
                {
                    var row = planSheet.RawRows[r];
                    for (int c = 0; c < row.Count; c++)
                    {
                        var cellVal = row[c]?.Trim() ?? string.Empty;

                        if (colApproveNo == -1 && (
                            cellVal.Contains("เลขที่ใบสั่ง") ||
                            cellVal.Contains("เลขที่") ||
                            cellVal.Contains("เลขใบสั่ง")))
                        {
                            colApproveNo = c;
                            headerRowIndex = Math.Max(headerRowIndex, r);
                        }

                        if (colDelivery == -1 && (
                            cellVal.Contains("ส่งมอบ") ||
                            cellVal.Contains("เป้าหมาย")))
                        {
                            colDelivery = c;
                            headerRowIndex = Math.Max(headerRowIndex, r);
                        }
                    }
                }

                // Fallback defaults if headers not found
                if (colApproveNo == -1) colApproveNo = 1;  // Column B
                if (colDelivery == -1) colDelivery = 12;    // Column M
                if (headerRowIndex == -1) headerRowIndex = 7;

                var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // ===== STEP 3: Loop File B rows (driver) and left-join File A =====
                for (int r = headerRowIndex + 1; r < planSheet.RawRows.Count; r++)
                {
                    var row = planSheet.RawRows[r];
                    if (row.Count == 0) continue;

                    string approveNo = GetColVal(row, colApproveNo);
                    if (string.IsNullOrEmpty(approveNo)) continue; // skip blank/summary rows

                    string key = CleanKey(approveNo);
                    bool isMatched = poLookup.TryGetValue(key, out var matchedPo);

                    if (isMatched)
                    {
                        matchedKeys.Add(key);
                    }

                    string deliveryDate = colDelivery < row.Count ? GetColVal(row, colDelivery) : "";

                    mergedRows.Add(new MergeResult
                    {
                        // Fields from File B (always present)
                        PoNumber = isMatched ? (matchedPo?.PoNumber ?? "") : "", // File A number (only if matched)
                        PlanOrderNo = approveNo,                        // File B number
                        ApprovedDate = ParseDateNullable(matchedPo?.ApprovedDate),
                        Urgency = matchedPo?.Urgency ?? "",
                        Quantity = ParseDecimalNullable(matchedPo?.RemarksQuantity),
                        Amount = ParseDecimalNullable(matchedPo?.Amount),
                        Remarks = matchedPo?.Remarks ?? "",
                        DeliveryTargetDate = FormatDeliveryTargetDate(deliveryDate),
                        IsMatched = isMatched
                    });
                }

                // ===== STEP 4: Add remaining items from File A (Request for Approval) that were not matched =====
                foreach (var kvp in poLookup)
                {
                    if (!matchedKeys.Contains(kvp.Key))
                    {
                        var unmatchedPo = kvp.Value;
                        mergedRows.Add(new MergeResult
                        {
                            // Fields from File A (always present since it comes from File A)
                            PoNumber = unmatchedPo.PoNumber, // File A number
                            PlanOrderNo = "",                 // File B number (missing)
                            ApprovedDate = ParseDateNullable(unmatchedPo.ApprovedDate),
                            Urgency = unmatchedPo.Urgency,
                            Quantity = ParseDecimalNullable(unmatchedPo.RemarksQuantity),
                            Amount = ParseDecimalNullable(unmatchedPo.Amount),
                            Remarks = unmatchedPo.Remarks,
                            DeliveryTargetDate = null,
                            IsMatched = false
                        });
                    }
                }
            }

            ViewData["SessionIdA"] = sessionIdA;
            ViewData["SessionIdB"] = sessionIdB;
            ViewData["FileNameA"] = fileA.FileName;
            ViewData["FileNameB"] = fileB.FileName;

            return View(mergedRows);
        }

        // Action to save the finalized matched/edited rows to the database
        [HttpPost]
        public IActionResult SaveMergeResult([FromBody] SaveMergeRequestModel model)
        {
            if (model == null || model.Rows == null || !model.Rows.Any())
            {
                return Json(new { success = false, error = "ไม่มีข้อมูลที่จะบันทึก" });
            }

            try
            {
                int matchedCount = model.Rows.Count(r => r.IsMatched);
                int unmatchedCount = model.Rows.Count(r => !r.IsMatched);

                // Try to get logged in UserId from claims, fallback to ADMIN01
                string userId = string.Empty;
                var claimUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!string.IsNullOrEmpty(claimUserId))
                {
                    userId = claimUserId;
                }
                else
                {
                    var defaultUser = _context.Users.FirstOrDefault(u => u.EmployeeCode == "ADMIN01");
                    userId = defaultUser?.Id ?? string.Empty;
                }

                // 1. Create a DB Session Record
                var session = new ImportSession
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    SourceFileName = string.IsNullOrWhiteSpace(model.ReportName) ? "รายงานเปรียบเทียบข้อมูล" : model.ReportName.Trim(),
                    CompareFileName = $"{model.FileNameA} และ {model.FileNameB}",
                    MatchedCount = matchedCount,
                    UnmatchedCount = unmatchedCount,
                    CreatedAt = DateTime.Now
                };
                _context.ImportSessions.Add(session);

                // 2. Insert rows linking to this session
                foreach (var row in model.Rows)
                {
                    var result = new MergeResult
                    {
                        ImportSessionId = session.Id,
                        PoNumber = row.PoNumber == "-" ? "" : row.PoNumber,
                        PlanOrderNo = row.PlanOrderNo == "-" ? "" : row.PlanOrderNo,
                        ApprovedDate = ParseDateNullable(row.ApprovedDate),
                        Urgency = row.Urgency == "-" ? "" : row.Urgency,
                        Quantity = ParseDecimalNullable(row.Quantity),
                        Amount = ParseDecimalNullable(row.Amount),
                        Remarks = row.Remarks == "-" ? "" : row.Remarks,
                        DeliveryTargetDate = FormatDeliveryTargetDate(row.DeliveryTargetDate),
                        IsMatched = row.IsMatched
                    };
                    _context.MergeResults.Add(result);
                }

                _context.SaveChanges();

                // 3. Clear temporary files from cache
                if (Guid.TryParse(model.SessionIdA, out var idA)) _storageService.DeleteImport(idA);
                if (Guid.TryParse(model.SessionIdB, out var idB)) _storageService.DeleteImport(idB);

                return Json(new { success = true, redirectUrl = Url.Action("Success", new { sessionId = session.Id }) });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = $"เกิดข้อผิดพลาดในการบันทึกข้อมูล: {ex.Message}" });
            }
        }

        public IActionResult Success(Guid sessionId)
        {
            ViewData["SessionId"] = sessionId;
            return View();
        }

        // Safe helper to read column value
        private string GetColVal(List<string> row, int index)
        {
            return index < row.Count ? (row[index]?.Trim() ?? string.Empty) : string.Empty;
        }

        // Helper to strip non-digit characters for matching keys
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

        // Helper to extract quantity from remarks text
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
                var match = System.Text.RegularExpressions.Regex.Match(remarks, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value;
                }
            }

            return "-";
        }

        private DateTime? ParseDateNullable(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (DateTime.TryParse(val, out var d)) return d;
            
            string[] formats = { "d/M/yyyy", "d/M/yy", "dd/MM/yyyy", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(val, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d2))
            {
                return d2;
            }
            return null;
        }

        private string? FormatDeliveryTargetDate(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return "";
            
            // Try to parse as date. If it is a valid date, format it as dd/MM/yyyy for consistency.
            // Otherwise, return the raw string (e.g. "นัดตอบเป้าหมาย7/7/26").
            if (DateTime.TryParse(val, out var d)) return d.ToString("dd/MM/yyyy");
            
            string[] formats = { "d/M/yyyy", "d/M/yy", "dd/MM/yyyy", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(val, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d2))
            {
                return d2.ToString("dd/MM/yyyy");
            }
            
            return val.Trim();
        }

        private decimal? ParseDecimalNullable(string? val)
        {
            if (string.IsNullOrWhiteSpace(val) || val == "-") return null;
            string clean = val.Replace("฿", "").Replace(",", "").Trim();
            if (decimal.TryParse(clean, out var dec)) return dec;
            return null;
        }
    }

    public class TempPartPo
    {
        public string PoNumber { get; set; } = string.Empty;
        public string RequestDate { get; set; } = string.Empty;
        public string ApprovedDate { get; set; } = string.Empty;
        public string Urgency { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
        public string RemarksQuantity { get; set; } = string.Empty;
    }

    public class SaveMergeRequestModel
    {
        public string SessionIdA { get; set; } = string.Empty;
        public string SessionIdB { get; set; } = string.Empty;
        public string FileNameA { get; set; } = string.Empty;
        public string FileNameB { get; set; } = string.Empty;
        public string ReportName { get; set; } = string.Empty;
        public List<SaveMergeRowModel> Rows { get; set; } = new();
    }

    public class SaveMergeRowModel
    {
        public string PoNumber { get; set; } = string.Empty;
        public string PlanOrderNo { get; set; } = string.Empty;
        public string ApprovedDate { get; set; } = string.Empty;
        public string Urgency { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string Remarks { get; set; } = string.Empty;
        public string DeliveryTargetDate { get; set; } = string.Empty;
        public bool IsMatched { get; set; }
    }
}
