using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using CostFlow.Data;
using CostFlow.Models;
using CostFlow.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CostFlow.Controllers
{
    [Authorize]
    public class StockController : Controller
    {
        private readonly AppDbContext _context;
        private readonly StockMatchingService _matchingService;
        private readonly IWebHostEnvironment _env;
        private readonly ISupabaseStorageService _supabaseStorage;

        public StockController(
            AppDbContext context, 
            StockMatchingService matchingService, 
            IWebHostEnvironment env,
            ISupabaseStorageService supabaseStorage)
        {
            _context = context;
            _matchingService = matchingService;
            _env = env;
            _supabaseStorage = supabaseStorage;
        }

        // หน้าต่างสำหรับ Admin ตรวจรับของเข้าคลัง (AI Matching)
        [HttpPost]
        public IActionResult Reconcile([FromForm] List<Guid> OrderIds)
        {
            if (OrderIds != null && OrderIds.Any())
            {
                return RedirectToAction("Index", "Stock", new { autoOpenReceive = true, orderId = OrderIds.First() });
            }
            return RedirectToAction("Index", "Stock");
        }

        // ดึงรายการที่รับของแล้วจาก Monthly Cost / PO แต่ยังไม่ได้นำเข้าสต๊อก
        [HttpGet]
        public async Task<IActionResult> GetPendingReceiveOrders([FromQuery] Guid? specificOrderId = null)
        {
            try
            {
                var receivedActions = await _context.MonthlyOrderActions
                    .Include(moa => moa.OrderTrackingMaster)
                    .Where(moa => moa.Action == "ReceivedFull" || moa.Action == "Received")
                    .OrderByDescending(moa => moa.CreatedAt)
                    .ToListAsync();

                var reconciledRefs = await _context.StockLogs
                    .Where(l => l.Action == "IN_WO" && !string.IsNullOrEmpty(l.ReferenceId))
                    .Select(l => l.ReferenceId!)
                    .Distinct()
                    .ToListAsync();
                var reconciledSet = new HashSet<string>(reconciledRefs, StringComparer.OrdinalIgnoreCase);

                var items = new List<PendingReceiptItemDto>();

                foreach (var act in receivedActions)
                {
                    var otm = act.OrderTrackingMaster;
                    if (otm == null) continue;

                    decimal qty = 1;
                    if (!string.IsNullOrEmpty(otm.RemarksQuantity))
                    {
                        var digits = new string(otm.RemarksQuantity.Where(char.IsDigit).ToArray());
                        if (decimal.TryParse(digits, out var parsedQty) && parsedQty > 0)
                        {
                            qty = parsedQty;
                        }
                    }

                    var isReconciled = !string.IsNullOrEmpty(otm.PoNumber) && reconciledSet.Contains(otm.PoNumber);

                    items.Add(new PendingReceiptItemDto
                    {
                        OrderId = otm.Id,
                        PoNumber = otm.PoNumber ?? "-",
                        OrderName = !string.IsNullOrWhiteSpace(otm.Remarks) ? otm.Remarks.Trim() : "ไม่ระบุชื่อสินค้า",
                        Quantity = qty,
                        Unit = "ชิ้น",
                        MonthYear = act.MonthYear,
                        Amount = otm.Amount ?? "0.00",
                        ApprovedDate = otm.ApprovedDate,
                        IsReconciled = isReconciled,
                        CreatedAt = act.CreatedAt
                    });
                }

                var pendingList = items.Where(x => !x.IsReconciled).ToList();
                var doneList = items.Where(x => x.IsReconciled).ToList();

                var finalList = new List<PendingReceiptItemDto>();

                if (specificOrderId.HasValue)
                {
                    var target = items.FirstOrDefault(x => x.OrderId == specificOrderId.Value);
                    if (target != null)
                    {
                        finalList.Add(target);
                        pendingList.RemoveAll(x => x.OrderId == specificOrderId.Value);
                        doneList.RemoveAll(x => x.OrderId == specificOrderId.Value);
                    }
                }

                finalList.AddRange(pendingList);
                finalList.AddRange(doneList);

                return Json(new
                {
                    success = true,
                    pendingCount = pendingList.Count + (specificOrderId.HasValue && !pendingList.Any(x => x.OrderId == specificOrderId.Value) ? 1 : 0),
                    items = finalList
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message, items = new List<PendingReceiptItemDto>() });
            }
        }

        [HttpPost]
        public async Task<IActionResult> ConfirmReconcile([FromBody] ConfirmReconcileRequest request)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                
                foreach (var item in request.Items)
                {
                    // ถ้าผู้ใช้เลือก "ไม่มีในโกดัง" จะไม่ทำอะไรกับสต๊อก ข้ามไปเลย
                    if (string.IsNullOrEmpty(item.SelectedStockProductCode))
                        continue;

                    // 1. อัปเดตยอดคงเหลือในโกดัง
                    var stockItem = await _context.StockItems
                        .FirstOrDefaultAsync(s => s.ProductCode == item.SelectedStockProductCode);
                        
                    if (stockItem != null)
                    {
                        stockItem.Quantity += item.ReceivedQuantity;
                        stockItem.UpdatedAt = DateTime.UtcNow;

                        var otm = await _context.OrderTrackingMasters.FirstOrDefaultAsync(o => o.Id == item.OrderId);

                        // 2. บันทึกประวัติลง StockLogs
                        _context.StockLogs.Add(new StockLog
                        {
                            StockItemCode = stockItem.ProductCode,
                            Action = "IN_WO",
                            QuantityChanged = item.ReceivedQuantity, // เป็นค่าบวกคือรับเข้า
                            User = userId ?? "System",
                            ReferenceId = otm?.PoNumber,
                            Remarks = $"รับสินค้าจาก WO ({item.OrderName})",
                            Timestamp = DateTime.UtcNow
                        });
                    }

                    // 3. จำคำศัพท์ใหม่ลง ItemMappings (ถ้าคำนี้ยังไม่เคยมี)
                    var existingMapping = await _context.ItemMappings
                        .FirstOrDefaultAsync(m => m.OrderName == item.OrderName);
                    
                    if (existingMapping == null)
                    {
                        _context.ItemMappings.Add(new ItemMapping
                        {
                            OrderName = item.OrderName,
                            StockItemCode = item.SelectedStockProductCode,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                    else if (existingMapping.StockItemCode != item.SelectedStockProductCode)
                    {
                        // ถ้าเคยมีแต่เปลี่ยนใจแก้ Map ใหม่
                        existingMapping.StockItemCode = item.SelectedStockProductCode;
                    }
                }

                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        // ดูไฟล์เอกสารแนบของสินค้า (รองรับทั้งไฟล์ใน uploads และตรวจเช็กไฟล์เดิม)
        [HttpGet]
        public IActionResult ViewFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return NotFound();
            }

            var cleanPath = path.TrimStart('/', '\\');
            var fullPath = Path.Combine(_env.WebRootPath, cleanPath);
            if (System.IO.File.Exists(fullPath))
            {
                var ext = Path.GetExtension(fullPath).ToLower();
                var contentType = ext switch
                {
                    ".pdf" => "application/pdf",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream"
                };
                return PhysicalFile(fullPath, contentType);
            }

            var fileNameOnly = Path.GetFileName(path);
            return Content($@"
                <!DOCTYPE html>
                <html lang='th'>
                <head>
                    <meta charset='utf-8'>
                    <title>ไม่พบไฟล์ - CostFlow</title>
                    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                    <style>
                        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; background: #f8fafc; color: #1e293b; }}
                        .card {{ background: white; border: 1px solid #e2e8f0; border-radius: 12px; padding: 32px; max-width: 480px; text-align: center; box-shadow: 0 4px 6px -1px rgb(0 0 0 / 0.1); }}
                        h3 {{ margin-top: 0; color: #0f172a; font-size: 18px; }}
                        p {{ font-size: 14px; color: #64748b; line-height: 1.5; }}
                        .file-badge {{ background: #f1f5f9; padding: 6px 12px; border-radius: 6px; font-family: monospace; font-size: 12px; color: #00288e; word-break: break-all; margin: 12px 0; display: inline-block; }}
                        button {{ background: #00288e; color: white; border: none; padding: 8px 20px; border-radius: 6px; cursor: pointer; font-size: 14px; font-weight: 500; transition: background 0.2s; }}
                        button:hover {{ background: #001f70; }}
                    </style>
                </head>
                <body>
                    <div class='card'>
                        <h3>ไม่พบไฟล์เอกสารจริงในเซิร์ฟเวอร์</h3>
                        <div class='file-badge'>{System.Net.WebUtility.HtmlEncode(fileNameOnly)}</div>
                        <p>ไฟล์นี้เป็นชื่ออ้างอิงจากข้อมูลเดิมในระบบ Excel (ยังไม่มีการอัปโหลดไฟล์จริงเก็บไว้ในระบบเว็บ)<br>คุณสามารถกดปุ่ม <b>แก้ไข</b> ในหน้ารายการสินค้าเพื่ออัปโหลดไฟล์จริงเข้ามาได้ครับ</p>
                        <button onclick='window.close()'>ปิดหน้านี้</button>
                    </div>
                </body>
                </html>", "text/html; charset=utf-8");
        }

        // หน้าต่าง Dashboard โกดัง สำหรับ Staff
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var stockItems = await _context.StockItems.ToListAsync();

            try
            {
                var reconciledRefs = await _context.StockLogs
                    .Where(l => l.Action == "IN_WO" && !string.IsNullOrEmpty(l.ReferenceId))
                    .Select(l => l.ReferenceId!)
                    .Distinct()
                    .ToListAsync();
                var reconciledSet = new HashSet<string>(reconciledRefs, StringComparer.OrdinalIgnoreCase);

                var pendingCount = await _context.MonthlyOrderActions
                    .Include(moa => moa.OrderTrackingMaster)
                    .Where(moa => (moa.Action == "ReceivedFull" || moa.Action == "Received") &&
                                  moa.OrderTrackingMaster != null &&
                                  !reconciledSet.Contains(moa.OrderTrackingMaster.PoNumber))
                    .CountAsync();

                ViewBag.PendingReceiveCount = pendingCount;
            }
            catch
            {
                ViewBag.PendingReceiveCount = 0;
            }

            return View(stockItems);
        }

        // เพิ่มสินค้าใหม่เข้าระบบสต๊อก
        [HttpPost]
        public async Task<IActionResult> AddItem([FromBody] AddStockItemDto dto)
        {
            if (dto == null)
            {
                return Json(new { success = false, message = "ไม่พบข้อมูลที่ส่งมา" });
            }

            if (string.IsNullOrWhiteSpace(dto.ProductCode))
            {
                return Json(new { success = false, message = "กรุณาระบุรหัสสินค้า" });
            }

            if (string.IsNullOrWhiteSpace(dto.ProductName))
            {
                return Json(new { success = false, message = "กรุณาระบุชื่อสินค้า" });
            }

            var trimmedCode = dto.ProductCode.Trim();
            var trimmedName = dto.ProductName.Trim();

            // ตรวจสอบรหัสสินค้าซ้ำ
            var existing = await _context.StockItems
                .AnyAsync(s => s.ProductCode.ToLower() == trimmedCode.ToLower());

            if (existing)
            {
                return Json(new { success = false, message = $"รหัสสินค้า '{trimmedCode}' มีอยู่ในระบบแล้ว กรุณาใช้รหัสอื่น" });
            }

            var stockGroup = string.IsNullOrWhiteSpace(dto.StockGroup) ? "เบ็ดเตล็ด" : dto.StockGroup.Trim();
            var category = string.IsNullOrWhiteSpace(dto.Category) ? "อะไหล่" : dto.Category.Trim();

            // คำนวณสถานะสต๊อก
            string status = "สต๊อกเพียงพอ";
            if (dto.Quantity <= 0)
            {
                status = "สต๊อกหมด";
            }
            else if (dto.MinStock > 0 && dto.Quantity <= dto.MinStock)
            {
                status = "สต๊อกใกล้หมด";
            }

            var newItem = new StockItem
            {
                ProductCode = trimmedCode,
                ProductName = trimmedName,
                Category = category,
                StockGroup = stockGroup,
                FilePath = !string.IsNullOrWhiteSpace(dto.FilePath) ? dto.FilePath.Trim() : null,
                Quantity = dto.Quantity >= 0 ? dto.Quantity : 0,
                MinStock = dto.MinStock >= 0 ? dto.MinStock : 0,
                MaxStock = dto.MaxStock >= 0 ? dto.MaxStock : 0,
                StockStatus = status,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.StockItems.Add(newItem);

            // บันทึกประวัติการเพิ่มสินค้าใหม่ครั้งแรกลง StockLogs
            var userId = User.Identity?.Name ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "Staff";
            _context.StockLogs.Add(new StockLog
            {
                StockItemCode = newItem.ProductCode,
                Action = "MANUAL_ADD",
                QuantityChanged = newItem.Quantity,
                User = userId,
                Remarks = "เพิ่มสินค้าใหม่เข้าระบบ",
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "เพิ่มสินค้าใหม่เรียบร้อยแล้ว", item = newItem });
        }

        // แก้ไขข้อมูลสินค้าในสต๊อก (รหัส, ชื่อ, หมวดหมู่, สต๊อกขั้นต่ำ, สต๊อกสูงสุด, สต๊อกปัจจุบัน)
        [HttpPost]
        public async Task<IActionResult> UpdateItem([FromBody] UpdateStockItemDto dto)
        {
            if (dto == null)
            {
                return Json(new { success = false, message = "ไม่พบข้อมูลที่ส่งมา" });
            }

            if (dto.Id <= 0)
            {
                return Json(new { success = false, message = "ไม่พบรหัสอ้างอิงสินค้าในระบบ" });
            }

            if (string.IsNullOrWhiteSpace(dto.ProductCode))
            {
                return Json(new { success = false, message = "กรุณาระบุรหัสสินค้า" });
            }

            if (string.IsNullOrWhiteSpace(dto.ProductName))
            {
                return Json(new { success = false, message = "กรุณาระบุชื่อสินค้า" });
            }

            var item = await _context.StockItems.FirstOrDefaultAsync(s => s.Id == dto.Id);
            if (item == null)
            {
                return Json(new { success = false, message = "ไม่พบรายการสินค้านี้ในฐานข้อมูล" });
            }

            var trimmedCode = dto.ProductCode.Trim();
            var trimmedName = dto.ProductName.Trim();

            // ตรวจสอบรหัสสินค้าซ้ำ (ถ้าแก้รหัสสินค้า ต้องไม่ซ้ำกับ Id อื่น)
            if (!string.Equals(item.ProductCode, trimmedCode, StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = await _context.StockItems
                    .AnyAsync(s => s.Id != dto.Id && s.ProductCode.ToLower() == trimmedCode.ToLower());
                if (duplicate)
                {
                    return Json(new { success = false, message = $"รหัสสินค้า '{trimmedCode}' มีอยู่ในระบบแล้ว กรุณาใช้รหัสอื่น" });
                }

                // อัปเดตตารางที่เกี่ยวข้อง (StockLogs, ItemMappings)
                var oldCode = item.ProductCode;
                var logsToUpdate = await _context.StockLogs.Where(l => l.StockItemCode == oldCode).ToListAsync();
                foreach (var log in logsToUpdate)
                {
                    log.StockItemCode = trimmedCode;
                }

                var mappingsToUpdate = await _context.ItemMappings.Where(m => m.StockItemCode == oldCode).ToListAsync();
                foreach (var mapping in mappingsToUpdate)
                {
                    mapping.StockItemCode = trimmedCode;
                }
            }

            var stockGroup = string.IsNullOrWhiteSpace(dto.StockGroup) ? (item.StockGroup ?? "เบ็ดเตล็ด") : dto.StockGroup.Trim();
            var category = string.IsNullOrWhiteSpace(dto.Category) ? (item.Category ?? "อะไหล่") : dto.Category.Trim();
            var newQuantity = dto.Quantity >= 0 ? dto.Quantity : 0;
            var minStock = dto.MinStock >= 0 ? dto.MinStock : 0;
            var maxStock = dto.MaxStock >= 0 ? dto.MaxStock : 0;

            // คำนวณสถานะสต๊อก
            string status = "สต๊อกเพียงพอ";
            if (newQuantity <= 0)
            {
                status = "สต๊อกหมด";
            }
            else if (minStock > 0 && newQuantity <= minStock)
            {
                status = "สต๊อกใกล้หมด";
            }

            var oldQuantity = item.Quantity;
            var qtyDiff = newQuantity - oldQuantity;

            // อัปเดตข้อมูลสินค้า
            item.ProductCode = trimmedCode;
            item.ProductName = trimmedName;
            item.Category = category;
            item.StockGroup = stockGroup;
            if (dto.FilePath != null)
            {
                item.FilePath = string.IsNullOrWhiteSpace(dto.FilePath) ? null : dto.FilePath.Trim();
            }
            item.MinStock = minStock;
            item.MaxStock = maxStock;
            item.Quantity = newQuantity;
            item.StockStatus = status;
            item.UpdatedAt = DateTime.UtcNow;

            // บันทึกประวัติการแก้ไขลง StockLogs
            var userId = User.Identity?.Name ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "Staff";
            _context.StockLogs.Add(new StockLog
            {
                StockItemCode = item.ProductCode,
                Action = "MANUAL_EDIT",
                QuantityChanged = qtyDiff,
                User = userId,
                Remarks = $"แก้ไขข้อมูลสินค้า (ยอดเดิม {oldQuantity:N0} -> {newQuantity:N0})",
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return Json(new { 
                success = true, 
                message = "บันทึกการแก้ไขข้อมูลสินค้าเรียบร้อยแล้ว",
                item = new {
                    item.Id,
                    item.ProductCode,
                    item.ProductName,
                    item.Category,
                    item.StockGroup,
                    item.MinStock,
                    item.MaxStock,
                    item.Quantity,
                    item.StockStatus
                }
            });
        }

        // ลบสินค้าออกจากสต๊อก (สำหรับ Admin เท่านั้น)
        [HttpPost]
        public async Task<IActionResult> DeleteItem([FromBody] DeleteStockItemDto dto)
        {
            if (!IsAdminUser())
            {
                return Json(new { success = false, message = "คุณไม่มีสิทธิ์ในการลบสินค้า (เฉพาะผู้ดูแลระบบเท่านั้น)" });
            }

            if (dto == null || dto.Id <= 0)
            {
                return Json(new { success = false, message = "ไม่พบรหัสสินค้าที่ต้องการลบ" });
            }

            var item = await _context.StockItems.FirstOrDefaultAsync(s => s.Id == dto.Id);
            if (item == null)
            {
                return Json(new { success = false, message = "ไม่พบรายการสินค้านี้ในระบบสต๊อก หรืออาจถูกลบไปแล้ว" });
            }

            var productCode = item.ProductCode;
            var productName = item.ProductName;

            // บันทึกประวัติการลบลง StockLogs
            var userId = User.Identity?.Name ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "Admin";
            _context.StockLogs.Add(new StockLog
            {
                StockItemCode = productCode,
                Action = "DELETE_ITEM",
                QuantityChanged = -item.Quantity,
                User = userId,
                Remarks = $"ลบสินค้า '{productName}' ({productCode}) ออกจากระบบสต๊อก",
                Timestamp = DateTime.UtcNow
            });

            // ลบ ItemMappings ที่ผูกกับรหัสสินค้านี้ (ถ้ามี)
            var mappings = await _context.ItemMappings.Where(m => m.StockItemCode == productCode).ToListAsync();
            if (mappings.Any())
            {
                _context.ItemMappings.RemoveRange(mappings);
            }

            _context.StockItems.Remove(item);
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = $"ลบสินค้า '{productName}' ({productCode}) เรียบร้อยแล้ว"
            });
        }

        // อัปโหลดรูปภาพสินค้าสต๊อก (ทั้งจากคลังภาพ และภาพถ่ายจากกล้อง) ขึ้น Supabase Storage
        [HttpPost]
        public async Task<IActionResult> UploadStockImage(IFormFile? file)
        {
            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "ไม่พบไฟล์รูปภาพที่ต้องการอัปโหลด" });
            }

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
            var extension = Path.GetExtension(file.FileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension) || !allowedExtensions.Contains(extension))
            {
                extension = ".jpg";
            }

            if (file.Length > 10 * 1024 * 1024)
            {
                return Json(new { success = false, message = "ขนาดไฟล์รูปภาพต้องไม่เกิน 10 MB" });
            }

            try
            {
                // 1. อัปโหลดขึ้น Supabase Storage (คลาวด์หลัก)
                using var stream = file.OpenReadStream();
                var contentType = file.ContentType ?? (extension == ".png" ? "image/png" : "image/jpeg");
                var publicUrl = await _supabaseStorage.UploadFileAsync(stream, file.FileName, contentType);

                return Json(new
                {
                    success = true,
                    imageUrl = publicUrl,
                    message = "อัปโหลดรูปภาพขึ้น Cloud Storage เรียบร้อยแล้ว"
                });
            }
            catch (Exception ex)
            {
                // 2. หากคลาวด์มีปัญหา ให้ Fallback บันทึกลงเครื่องเซิร์ฟเวอร์สำรองชั่วคราว
                try
                {
                    var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                    var uploadsFolder = Path.Combine(webRoot, "uploads", "stock");
                    if (!Directory.Exists(uploadsFolder))
                    {
                        Directory.CreateDirectory(uploadsFolder);
                    }

                    var fileName = $"stock_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}{extension}";
                    var fullPath = Path.Combine(uploadsFolder, fileName);

                    using (var localStream = new FileStream(fullPath, FileMode.Create))
                    {
                        await file.CopyToAsync(localStream);
                    }

                    var relativeUrl = $"/uploads/stock/{fileName}";
                    return Json(new
                    {
                        success = true,
                        imageUrl = relativeUrl,
                        message = "อัปโหลดรูปภาพลงเซิร์ฟเวอร์สำรองเรียบร้อยแล้ว"
                    });
                }
                catch
                {
                    return Json(new { success = false, message = $"เกิดข้อผิดพลาดในการบันทึกรูปภาพ: {ex.Message}" });
                }
            }
        }

        private bool IsAdminUser()
        {
            var userName = User.Identity?.Name ?? "";
            return User.IsInRole("Admin") || User.IsInRole("Dev") || User.IsInRole("Staff") ||
                   userName.Equals("ADMIN01", StringComparison.OrdinalIgnoreCase) ||
                   userName.Equals("DEV01", StringComparison.OrdinalIgnoreCase) ||
                   userName.Equals("STAFF01", StringComparison.OrdinalIgnoreCase);
        }

        // ตรวจสอบและส่งสัญญาณ Ping Keep-Alive ไปยัง Supabase (ป้องกันโปรเจกต์ Free Tier หลับ)
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> PingSupabase()
        {
            var isAlive = await _supabaseStorage.PingKeepAliveAsync();
            return Json(new
            {
                success = isAlive,
                message = isAlive ? "Supabase ตอบรับสัญญาณ Heartbeat เรียบร้อย (โปรเจกต์ตื่นอยู่ตลอดเวลา)" : "ไม่สามารถส่งสัญญาณไปยัง Supabase ได้",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        // ดึงประวัติ StockLogs ของสินค้าตาม ProductCode สำหรับเปิดดูใน Popup ประวัติการเบิก/จ่าย
        [HttpGet]
        public async Task<IActionResult> GetItemLogs(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                return Json(new { success = false, message = "ไม่พบรหัสสินค้า" });
            }

            var trimmedCode = productCode.Trim();
            var stockItem = await _context.StockItems
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ProductCode == trimmedCode);

            var logs = await _context.StockLogs
                .AsNoTracking()
                .Where(l => l.StockItemCode == trimmedCode)
                .OrderByDescending(l => l.Timestamp)
                .ThenByDescending(l => l.Id)
                .Select(l => new
                {
                    l.Id,
                    l.StockItemCode,
                    l.Action,
                    QuantityChanged = l.QuantityChanged,
                    ReferenceId = l.ReferenceId ?? "-",
                    Remarks = l.Remarks ?? "-",
                    Timestamp = l.Timestamp.ToString("dd/MM/yyyy HH:mm"),
                    User = l.User ?? "-"
                })
                .ToListAsync();

            return Json(new
            {
                success = true,
                item = stockItem == null ? null : new
                {
                    stockItem.ProductCode,
                    stockItem.ProductName,
                    stockItem.Category,
                    stockItem.Quantity,
                    stockItem.MinStock,
                    stockItem.MaxStock,
                    stockItem.StockStatus
                },
                logs = logs
            });
        }

        [HttpPost]
        public async Task<IActionResult> SimulateReceiveItem([FromBody] SimulateReceiveDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.RawText))
            {
                return Json(new { success = false, message = "กรุณาระบุข้อความรายการสินค้า" });
            }

            var (cleanInfo, matches) = await _matchingService.CleanAndFindMatchesAsync(dto.RawText, 5);

            var matchesDto = matches.Select(m => new
            {
                productCode = m.StockItem.ProductCode,
                productName = m.StockItem.ProductName,
                category = m.StockItem.Category,
                quantity = m.StockItem.Quantity,
                minStock = m.StockItem.MinStock,
                maxStock = m.StockItem.MaxStock,
                stockStatus = m.StockItem.StockStatus,
                matchPercentage = m.MatchPercentage,
                isExactMapping = m.IsExactMapping,
                isExactNameMatch = m.IsExactNameMatch
            }).ToList();

            return Json(new
            {
                success = true,
                rawText = cleanInfo.RawText,
                cleanName = cleanInfo.CleanName,
                detectedQuantity = cleanInfo.DetectedQuantity,
                detectedUnit = cleanInfo.DetectedUnit ?? "ชิ้น",
                extractedTargetDate = cleanInfo.ExtractedTargetDate,
                extractedRemarks = cleanInfo.ExtractedRemarks,
                matches = matchesDto
            });
        }

        [HttpPost]
        public async Task<IActionResult> QuickConfirmReceive([FromBody] ConfirmSingleReceiveDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.ProductCode))
            {
                return Json(new { success = false, message = "กรุณาระบุรหัสสินค้าที่ต้องการรับเข้าสต๊อก" });
            }

            if (dto.Quantity <= 0)
            {
                return Json(new { success = false, message = "จำนวนสินค้าต้องมากกว่า 0" });
            }

            var stockItem = await _context.StockItems.FirstOrDefaultAsync(s => s.ProductCode == dto.ProductCode);
            if (stockItem == null)
            {
                return Json(new { success = false, message = $"ไม่พบรหัสสินค้า {dto.ProductCode} ในระบบสต๊อก" });
            }

            var userId = User.Identity?.Name ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "Admin";

            // 1. อัปเดตยอดคงเหลือ
            stockItem.Quantity += dto.Quantity;
            stockItem.UpdatedAt = DateTime.UtcNow;

            // คำนวณ StockStatus
            if (stockItem.Quantity <= 0)
                stockItem.StockStatus = "สต๊อกหมด";
            else if (stockItem.MinStock > 0 && stockItem.Quantity <= stockItem.MinStock)
                stockItem.StockStatus = "สต๊อกใกล้หมด";
            else
                stockItem.StockStatus = "สต๊อกเพียงพอ";

            // 2. บันทึกประวัติลง StockLogs
            _context.StockLogs.Add(new StockLog
            {
                StockItemCode = stockItem.ProductCode,
                Action = "IN_WO",
                QuantityChanged = dto.Quantity,
                User = userId,
                ReferenceId = dto.ReferenceId ?? "MOCK-RECEIVE",
                Remarks = string.IsNullOrWhiteSpace(dto.Remarks)
                    ? $"รับของเข้าคลัง (จาก: {dto.OrderName})"
                    : dto.Remarks,
                Timestamp = DateTime.UtcNow
            });

            // 3. จำคำศัพท์ลง ItemMappings (ถ้ามีชื่อส่งมา และยังไม่เคยมีบันทึก)
            if (!string.IsNullOrWhiteSpace(dto.OrderName))
            {
                var normOrderName = _matchingService.NormalizeText(dto.OrderName);
                var allMappings = await _context.ItemMappings.ToListAsync();
                var exists = allMappings.Any(m => _matchingService.NormalizeText(m.OrderName) == normOrderName);
                if (!exists)
                {
                    _context.ItemMappings.Add(new ItemMapping
                    {
                        OrderName = dto.OrderName.Trim(),
                        StockItemCode = stockItem.ProductCode,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = $"รับสินค้า {stockItem.ProductName} ({dto.Quantity} ชิ้น) เข้าสต๊อกเรียบร้อยแล้ว",
                productCode = stockItem.ProductCode,
                productName = stockItem.ProductName,
                newQuantity = stockItem.Quantity,
                newStatus = stockItem.StockStatus
            });
        }

        // ปรับยอดสต๊อกด่วน (Inline Quantity Edit) สำหรับ Staff และผู้ใช้งานหน้างาน
        [HttpPost]
        public async Task<IActionResult> QuickUpdateQuantity([FromBody] QuickUpdateQuantityDto dto)
        {
            if (dto == null || dto.Id <= 0)
            {
                return Json(new { success = false, message = "ข้อมูลอ้างอิงสินค้าไม่ถูกต้อง" });
            }

            if (dto.Quantity < 0)
            {
                return Json(new { success = false, message = "จำนวนสต๊อกต้องไม่ติดลบ" });
            }

            var item = await _context.StockItems.FirstOrDefaultAsync(s => s.Id == dto.Id);
            if (item == null)
            {
                return Json(new { success = false, message = "ไม่พบรายการสินค้านี้ในระบบสต๊อก" });
            }

            var oldQuantity = item.Quantity;
            var qtyDiff = dto.Quantity - oldQuantity;

            // คำนวณ StockStatus
            string status = "สต๊อกเพียงพอ";
            if (dto.Quantity <= 0)
            {
                status = "สต๊อกหมด";
            }
            else if (item.MinStock > 0 && dto.Quantity <= item.MinStock)
            {
                status = "สต๊อกใกล้หมด";
            }

            item.Quantity = dto.Quantity;
            item.StockStatus = status;
            item.UpdatedAt = DateTime.UtcNow;

            // บันทึกประวัติลง StockLogs
            var userId = User.Identity?.Name ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "Staff";
            _context.StockLogs.Add(new StockLog
            {
                StockItemCode = item.ProductCode,
                Action = "MANUAL_EDIT",
                QuantityChanged = qtyDiff,
                User = userId,
                Remarks = $"ปรับยอดคงเหลือหน้างาน (ยอดเดิม {oldQuantity:N0} -> {dto.Quantity:N0})",
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                id = item.Id,
                productCode = item.ProductCode,
                newQuantity = item.Quantity,
                newStatus = item.StockStatus,
                message = $"ปรับยอดสินค้า {item.ProductName} เป็น {item.Quantity:N0} ชิ้น เรียบร้อยแล้ว"
            });
        }
    }

    public class QuickUpdateQuantityDto
    {
        public int Id { get; set; }
        public decimal Quantity { get; set; }
    }

    public class AddStockItemDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? StockGroup { get; set; }
        public string? FilePath { get; set; }
        public decimal Quantity { get; set; } = 0;
        public decimal MinStock { get; set; } = 0;
        public decimal MaxStock { get; set; } = 0;
    }

    public class UpdateStockItemDto
    {
        public int Id { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? StockGroup { get; set; }
        public string? FilePath { get; set; }
        public decimal MinStock { get; set; } = 0;
        public decimal MaxStock { get; set; } = 0;
        public decimal Quantity { get; set; } = 0; // สต๊อกปัจจุบัน
    }

    public class DeleteStockItemDto
    {
        public int Id { get; set; }
    }

    public class SimulateReceiveDto
    {
        public string RawText { get; set; } = string.Empty;
    }

    public class ConfirmSingleReceiveDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; } = 1;
        public string OrderName { get; set; } = string.Empty;
        public string? ReferenceId { get; set; }
        public string? Remarks { get; set; }
    }

    public class PendingReceiptItemDto
    {
        public Guid OrderId { get; set; }
        public string PoNumber { get; set; } = string.Empty;
        public string OrderName { get; set; } = string.Empty;
        public decimal Quantity { get; set; } = 1;
        public string Unit { get; set; } = "ชิ้น";
        public string MonthYear { get; set; } = string.Empty;
        public string Amount { get; set; } = "0.00";
        public string? ApprovedDate { get; set; }
        public bool IsReconciled { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
