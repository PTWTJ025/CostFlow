using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace CostFlow.Models
{
    /// <summary>
    /// ผู้ใช้งานระบบ — สืบทอดจาก IdentityUser เพื่อใช้ ASP.NET Core Identity
    /// Identity จะสร้างตาราง AspNetUsers ให้อัตโนมัติ พร้อม PasswordHash, SecurityStamp ฯลฯ
    /// เราเพิ่มเฉพาะ field ที่ธุรกิจต้องการเท่านั้น
    /// </summary>
    public class ApplicationUser : IdentityUser
    {
        public string EmployeeCode { get; set; } = string.Empty; // รหัสพนักงาน (Unique) — ใช้ login
        public string FullName { get; set; } = string.Empty;     // ชื่อ-นามสกุล
        public string? ProfilePictureUrl { get; set; }          // URL รูปโปรไฟล์
        public bool IsActive { get; set; } = true;               // สถานะพนักงาน (ปิดได้โดยไม่ลบข้อมูล)
        public DateTime CreatedAt { get; set; }
    }

    public class ImportSession
    {
        [Key]
        public Guid Id { get; set; }
        public string UserId { get; set; } = string.Empty;      // FK -> AspNetUsers.Id (string)
        public string SourceFileName { get; set; } = string.Empty;
        public string CompareFileName { get; set; } = string.Empty;
        public int MatchedCount { get; set; }
        public int UnmatchedCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class MergeResult
    {
        [Key]
        public int Id { get; set; }
        public Guid ImportSessionId { get; set; }
        public string PoNumber { get; set; } = string.Empty;
        public string PlanOrderNo { get; set; } = string.Empty;
        public DateTime? ApprovedDate { get; set; }
        public string? Urgency { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? Amount { get; set; }
        public string? Remarks { get; set; }
        public string? DeliveryTargetDate { get; set; }
        public bool IsMatched { get; set; }
    }

    public class ProductPrice
    {
        [Key]
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public double TotalQty { get; set; }
        public double TotalValue { get; set; }
        public double PricePerUnit { get; set; }
        public string Sources { get; set; } = string.Empty;
    }

    public class SparePartOrderBatch
    {
        [Key]
        public Guid Id { get; set; }
        public string BatchName { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;       // FK -> AspNetUsers.Id (string)
        public int ItemCount { get; set; }
        public decimal TotalAmount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class SparePartOrder
    {
        [Key]
        public int Id { get; set; }
        public Guid BatchId { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? Unit { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal TotalAmount { get; set; }
        public string? ApprovalNo { get; set; }
        public string? Remarks { get; set; }
        public DateTime? ReceiveDate { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
