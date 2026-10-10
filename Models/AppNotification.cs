using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CostFlow.Models
{
    public class AppNotification
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string Type { get; set; } = "SYSTEM"; // MONTHLY_COST, ORDER_TRACKING, STOCK_RECEIVE, STOCK_UPDATE

        [Required]
        [MaxLength(255)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [MaxLength(1000)]
        public string Message { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? TargetUrl { get; set; } // e.g. "/MonthlyCost", "/OrderTracking", "/Stock?search=...", or ""/null for no redirect

        public bool IsRead { get; set; } = false;

        [MaxLength(100)]
        public string? Category { get; set; } // "finance", "logistics", "stock"

        [MaxLength(50)]
        public string? ReferenceId { get; set; } // Reference OrderId, ProductCode, or BatchId

        [MaxLength(50)]
        public string? ActionUser { get; set; } // ผู้ทำรายการ

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReadAt { get; set; }
    }
}
