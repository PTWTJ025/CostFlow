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
    // ตารางรายงานหลัก (ไฟล์สั่งผลิต)
    public class Report
    {
        [Key]
        public Guid Id { get; set; }
        
        [Required]
        public string ReportName { get; set; } = string.Empty;
        
        public string OriginalFileName { get; set; } = string.Empty;
        public int TotalPOs { get; set; }
        public int MatchedPOs { get; set; }
        public DateTime CreatedAt { get; set; }
        
        // Navigation property
        public ICollection<OrderTrackingMaster> Orders { get; set; } = new List<OrderTrackingMaster>();
        public ICollection<WeeklyPlan> WeeklyPlans { get; set; } = new List<WeeklyPlan>();
    }

    // ตาราง PO จากไฟล์สั่งผลิต
    public class OrderTrackingMaster
    {
        [Key]
        public Guid Id { get; set; }
        
        [Required]
        public Guid ReportId { get; set; }
        
        [Required]
        public string PoNumber { get; set; } = string.Empty;
        public string? RequestDate { get; set; }
        public string? ApprovedDate { get; set; }
        public string? Urgency { get; set; }
        public string? Amount { get; set; }
        public string? Remarks { get; set; }
        public string? RemarksQuantity { get; set; }
        
        public string Status { get; set; } = "Pending";
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        
        // Navigation properties
        public Report Report { get; set; } = null!;
        public ICollection<WeeklyPlanDetail> MatchedInWeeklyPlans { get; set; } = new List<WeeklyPlanDetail>();
    }

    // ตารางไฟล์แผนผลิตสัปดาห์
    public class WeeklyPlan
    {
        [Key]
        public Guid Id { get; set; }
        
        [Required]
        public Guid ReportId { get; set; }
        
        public string FileName { get; set; } = string.Empty;
        public string SheetName { get; set; } = string.Empty;
        public int TotalRecords { get; set; }
        public int MatchedCount { get; set; }
        public DateTime UploadedAt { get; set; }
        
        // Navigation properties
        public Report Report { get; set; } = null!;
        public ICollection<WeeklyPlanDetail> Details { get; set; } = new List<WeeklyPlanDetail>();
    }

    // ตารางรายละเอียด PO ในไฟล์แผนผลิต
    public class WeeklyPlanDetail
    {
        [Key]
        public Guid Id { get; set; }
        
        [Required]
        public Guid WeeklyPlanId { get; set; }
        
        public string? PoNumberInFile { get; set; }
        public string? Department { get; set; }
        public string? OrderName { get; set; }
        public string? OrderStatus { get; set; }
        public string? DeliveryTarget { get; set; }
        public string? Price { get; set; }
        
        public int RowIndex { get; set; }
        public bool IsMatched { get; set; }
        public Guid? MatchedOrderId { get; set; }
        
        // Navigation properties
        public WeeklyPlan WeeklyPlan { get; set; } = null!;
        public OrderTrackingMaster? MatchedOrder { get; set; }
    }

    public class ReportSummaryViewModel
    {
        public Guid SessionId { get; set; }
        public string ReportName { get; set; } = string.Empty;
        public string CompareFileName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int TotalRows { get; set; }
        public int MatchedRows { get; set; }
    }
}
