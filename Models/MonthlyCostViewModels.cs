using System;
using System.Collections.Generic;

namespace CostFlow.Models
{
    // ViewModel สำหรับหน้า Index — แต่ละ card = 1 เดือน
    public class MonthlyCardViewModel
    {
        public string MonthYearDisplay { get; set; } = string.Empty; // "กรกฎาคม 2569"
        public string MonthYearKey { get; set; } = string.Empty;     // "2026-07"
        public string FileName { get; set; } = string.Empty;         // ชื่อไฟล์แผนผลิตที่เกี่ยวข้อง
        public string StatusLabel { get; set; } = string.Empty;      // "กำลังดำเนินการ" / "เสร็จสมบูรณ์" / "ค้างดำเนินการ"
        public string StatusCode { get; set; } = string.Empty;       // "active" / "completed" / "pending"

        // Progress
        public int TotalItems { get; set; }
        public int DoneItems { get; set; }   // ReceivedFull + Deferred
        public int PendingItems { get; set; } // ยังไม่ตัดสินใจ

        // Pill breakdown — actions decided IN this month
        public int ReceivedCount { get; set; }
        public decimal ReceivedAmount { get; set; }
        public int DeferredCount { get; set; }
        public decimal DeferredAmount { get; set; }
        public int SkippedCount { get; set; }
        public decimal SkippedAmount { get; set; }

        // Carry-over sub-counts — how many of the above came from a prior month (Deferred/Skipped)
        public int ReceivedCarryOver { get; set; }  // ค้างมาจากเดือนก่อน แล้วถูกรับในเดือนนี้
        public int DeferredCarryOver { get; set; }  // ค้างมาจากเดือนก่อน แล้วผ่อนต่ออีก (Skipped→Deferred)
        public int SkippedCarryOver { get; set; }   // ค้างมาจากเดือนก่อน แล้วยังไม่รับอีก

        // ยอดรวมทั้งหมดของสินค้าในเดือนนี้ (จาก OrderTrackingMasters โดยตรง ก่อน action)
        public decimal TotalAmount { get; set; }
        public decimal MonthAmount { get; set; }
        public bool IsLatestWithData { get; set; }
        public bool IsPastYearDecember { get; set; }

        public double ProgressPercent => TotalItems > 0
            ? Math.Round((double)DoneItems / TotalItems * 100, 1)
            : 0;
    }

    public class MonthlyCostIndexViewModel
    {
        public List<MonthlyCardViewModel> Cards { get; set; } = new();
    }

    public class MonthlyCostDetailViewModel
    {
        public string MonthYear { get; set; } = string.Empty;
        public List<PendingOrderItem> PendingOrders { get; set; } = new();
        public MonthlyStats Stats { get; set; } = new();
        public List<SavedOrderItem> SavedOrders { get; set; } = new();

        public bool IsCurrentMonth { get; set; }
        public bool IsFutureMonth { get; set; }
        public bool IsCompleted => Stats.TotalOrders > 0 && PendingOrders.Count == 0;
        public bool IsLocked => !IsCurrentMonth && IsCompleted;
    }

    public class PendingOrderItem
    {
        public Guid OrderId { get; set; }
        public string PoNumber { get; set; } = string.Empty;
        public string OrderName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }
        public string DeliveryTarget { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public string ForwardedStatus { get; set; } = string.Empty; // "Deferred", "Skipped", or ""
        public string ForwardedFromMonth { get; set; } = string.Empty; // e.g. "กรกฎาคม 2569"
        public string OriginalReportName { get; set; } = string.Empty; // e.g. "รายงานสั่งผลิต_13_ก.ค._2569"
        public string OriginalMonth { get; set; } = string.Empty; // เดือนต้นทางที่สินค้าถูกสร้างครั้งแรก (เช่น "มิถุนายน 2569")
    }

    public class SavedOrderItem
    {
        public Guid ActionId { get; set; }
        public Guid OrderId { get; set; }
        public string PoNumber { get; set; } = string.Empty;
        public string OrderName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal ActionPrice { get; set; }
        public string Action { get; set; } = string.Empty;
        public string DeliveryTarget { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string ForwardedStatus { get; set; } = string.Empty; 
        public string ForwardedFromMonth { get; set; } = string.Empty;
        public string OriginalMonth { get; set; } = string.Empty;
    }

    public class MonthlyStats
    {
        public int TotalOrders { get; set; }
        public decimal TotalPlannedAmount { get; set; }
        public int ProcessedOrders { get; set; }
        public decimal ProcessedAmount { get; set; }
        public decimal RemainingAmount => Math.Max(0m, TotalPlannedAmount - ProcessedAmount);
    }

    public class MonthlySummaryRow
    {
        public string MonthKey { get; set; } = string.Empty;
        public string MonthDisplay { get; set; } = string.Empty;
        public int ReceivedCount { get; set; }
        public decimal ReceivedAmount { get; set; }
        public int DeferredCount { get; set; }
        public decimal DeferredAmount { get; set; }
        public int SkippedCount { get; set; }
        public decimal TotalPaid { get; set; }
        public decimal Cumulative { get; set; }
        public decimal NewDebt { get; set; }
        public decimal DebtBalance { get; set; }
        public int CarryOverDeferredCount { get; set; }
        public decimal CarryOverDeferredAmount { get; set; }
        public bool HasData { get; set; }
    }

    public class MonthlySummaryViewModel
    {
        public int Year { get; set; }
        public List<MonthlySummaryRow> Rows { get; set; } = new();
        public decimal GrandTotal { get; set; }
        public decimal TotalReceived { get; set; }
        public decimal TotalDeferred { get; set; }
        public decimal TotalNewDebt { get; set; }
        public decimal CurrentDebt { get; set; }
        public decimal DebtAtYearStart { get; set; }
    }

    // ─── ViewModel สำหรับหน้า Summary (แสดงรายการสั่งซื้อทั้งหมดของปี) ───
    public class YearlyOrderSummaryViewModel
    {
        public int SelectedYear { get; set; }
        public List<int> AvailableYears { get; set; } = new();

        // Filters (ใช้เก็บค่าที่เลือกไว้)
        public int? FilterMonth { get; set; }
        public string? FilterStatus { get; set; }

        // รายการ orders ทั้งหมด
        public List<YearlyOrderItem> Items { get; set; } = new();

        // KPI (คำนวณจาก Items ทั้งหมดก่อน filter)
        public int TotalOrders { get; set; }
        public int TotalReceivedCount { get; set; }
        public decimal TotalReceivedAmount { get; set; }
        public int TotalDeferredCount { get; set; }
        public decimal TotalDeferredAmount { get; set; }
        public int TotalSkippedCount { get; set; }
        public decimal TotalSkippedAmount { get; set; }
    }

    public class YearlyOrderItem
    {
        public Guid ActionId { get; set; }
        public Guid OrderId { get; set; }
        public string PoNumber { get; set; } = "-";
        public string OrderName { get; set; } = "ไม่ระบุ";
        public string Quantity { get; set; } = "-";
        public string Department { get; set; } = "-";
        public decimal Amount { get; set; }
        public string MonthKey { get; set; } = string.Empty;   // "2026-08" (เดือนที่เกิด action จริง)
        public string MonthDisplay { get; set; } = string.Empty; // "สิงหาคม 2569"
        public int MonthNumber { get; set; } // 1-12 (เดือนที่เกิด action จริง)
        public string Action { get; set; } = string.Empty; // ReceivedFull / Deferred / Skipped
        public string ActionDisplay { get; set; } = string.Empty; // ภาษาไทย
        public string ApprovedDate { get; set; } = "-";
        public string ApprovedMonthDisplay { get; set; } = "-"; // "กรกฎาคม 2569"
        public int ApprovedMonthNumber { get; set; } // 1-12 สำหรับ filter

        // เดือนที่สินค้าโผล่ในหน้า Detail (สำหรับ Deferred = MonthYear+1, อื่นๆ = MonthYear)
        public int DisplayMonthNumber { get; set; } // 1-12
        public string DisplayMonthDisplay { get; set; } = string.Empty; // "กันยายน 2569"

        public DateTime CreatedAt { get; set; }

        // ── ข้อมูลเพิ่มเติมสำหรับหน้า Summary (ตามรูปแบบ Excel) ──
        public string DeliveryTarget { get; set; } = "-";        // เป้าหมายวันส่ง (จาก WeeklyPlanDetail)
        public string ReceivedDate { get; set; } = "-";          // ว.ด.ป. ที่รับของ (CreatedAt ของ ReceivedFull action)
        public decimal UnitPrice { get; set; }                   // ราคา/หน่วย (Amount / Qty)
        public string FullOrderName { get; set; } = "";          // ชื่อเต็ม (ใช้ใน tooltip)
    }
}

