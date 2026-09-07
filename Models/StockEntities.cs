using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CostFlow.Models
{
    // ตารางโกดังหลัก (ก้อน 2)
    public class StockItem
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string ProductCode { get; set; } = string.Empty;

        [Required]
        [MaxLength(255)]
        public string ProductName { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? Category { get; set; }

        [MaxLength(500)]
        public string? FilePath { get; set; } // ไฟล์เอกสาร / รูปภาพ / ลิงก์ (Cloud Storage URL บน Supabase / TiDB)

        public decimal InitialStock { get; set; } = 0; // สต๊อกเริ่มต้น

        public decimal Quantity { get; set; } = 0; // ยอดคงเหลือปัจจุบัน (VS_สต๊อกปัจจุบัน)

        public decimal MinStock { get; set; } = 0; // สต๊อกขั้นต่ำ

        public decimal MaxStock { get; set; } = 0; // สต๊อกสูงสุด

        [MaxLength(50)]
        public string? StockStatus { get; set; } // สถานะสต๊อก (เช่น สต๊อกเพียงพอ, ใกล้หมด)

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    // ตารางสะพานแปลภาษา (ก้อน 3)
    public class ItemMapping
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(500)]
        public string OrderName { get; set; } = string.Empty; // ชื่อที่คีย์มาจากหน้า MonthlyCost/WO

        [Required]
        [MaxLength(100)]
        public string StockItemCode { get; set; } = string.Empty; // รหัสอ้างอิงไปที่ StockItem.ProductCode

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // ตารางสมุดจดประวัติ (ก้อน 3)
    public class StockLog
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string StockItemCode { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Action { get; set; } = string.Empty; // เช่น "IN_WO", "OUT_MANUAL", "INITIAL_IMPORT"

        public decimal QuantityChanged { get; set; } = 0;

        [MaxLength(255)]
        public string? ReferenceId { get; set; } // เช่นรหัส WO26020061

        [MaxLength(500)]
        public string? Remarks { get; set; } // หมายเหตุเพิ่มเติม

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [MaxLength(255)]
        public string? User { get; set; }
    }
}
