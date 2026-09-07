using System;
using System.Collections.Generic;
using CostFlow.Services;

namespace CostFlow.Models
{
    public class StockReconcileViewModel
    {
        public List<ReconcileItemViewModel> Items { get; set; } = new List<ReconcileItemViewModel>();
        public string MonthYear { get; set; } = string.Empty;
    }

    public class ReconcileItemViewModel
    {
        public Guid OrderId { get; set; }
        public string PoNumber { get; set; } = string.Empty;
        public string OrderName { get; set; } = string.Empty;
        public int ReceivedQuantity { get; set; }
        public List<StockMatchResult> SuggestedMatches { get; set; } = new List<StockMatchResult>();
    }

    public class ConfirmReconcileRequest
    {
        public List<ConfirmReconcileItem> Items { get; set; } = new List<ConfirmReconcileItem>();
    }

    public class ConfirmReconcileItem
    {
        public Guid OrderId { get; set; }
        public string OrderName { get; set; } = string.Empty;
        public int ReceivedQuantity { get; set; }
        
        // ถัาผู้ใช้ติ๊ก "ไม่มีในโกดัง" ค่านี้จะเป็น null หรือว่างเปล่า
        public string? SelectedStockProductCode { get; set; }
    }
}
