using System;
using System.Collections.Generic;
using System.Linq;

namespace CostFlow.Models
{
    public class MonthlyCostSummaryViewModel
    {
        public int? SelectedYear { get; set; }
        public int? SelectedMonth { get; set; }
        public string Sort { get; set; } = "newest";
        public string? SelectedMonthKey { get; set; }
        public List<int> AvailableYears { get; set; } = new();
        public List<MonthlyCostSummaryRowViewModel> Months { get; set; } = new();
        public MonthlyCostSelectedDetailViewModel? SelectedDetail { get; set; }

        public decimal TotalExpense =>
            Months.Sum(x => x.TotalExpense);

        public int TotalTransactions =>
            Months.Sum(x => x.TotalTransactions);

        public decimal AverageExpense =>
            Months.Count == 0
                ? 0
                : TotalExpense / Months.Count;

        public MonthlyCostSummaryRowViewModel? HighestMonth =>
            Months
                .OrderByDescending(x => x.TotalExpense)
                .FirstOrDefault();
    }

    public class MonthlyCostSummaryRowViewModel
    {
        public string MonthYearKey { get; set; } = string.Empty;
        public int Year { get; set; }
        public int Month { get; set; }
        public string MonthName { get; set; } = string.Empty;

        public int BuddhistYear => Year + 543;

        public string DisplayName =>
            $"{MonthName} {BuddhistYear}";

        public int TotalTransactions { get; set; }
        public int ReceivedCount { get; set; }
        public decimal ReceivedAmount { get; set; }
        public int DeferredCount { get; set; }
        public decimal DeferredAmount { get; set; }
        public int SkippedCount { get; set; }
        public decimal SkippedAmount { get; set; }

        // ค่าใช้จ่ายจริงไม่นับรายการ Skipped
        public decimal TotalExpense =>
            ReceivedAmount + DeferredAmount;
    }

    public class MonthlyCostSelectedDetailViewModel
    {
        public string MonthYearKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public decimal TotalExpense { get; set; }
        public int TotalTransactions { get; set; }
        public int ReceivedCount { get; set; }
        public decimal ReceivedAmount { get; set; }
        public int DeferredCount { get; set; }
        public decimal DeferredAmount { get; set; }
        public int SkippedCount { get; set; }
        public decimal SkippedAmount { get; set; }

        public List<MonthlyCostSummaryItemViewModel> Items { get; set; } = new();
    }

    public class MonthlyCostSummaryItemViewModel
    {
        public Guid ActionId { get; set; }
        public string PoNumber { get; set; } = "-";
        public string OrderName { get; set; } = "ไม่ระบุ";
        public int Quantity { get; set; }
        public string Department { get; set; } = "-";
        public string Action { get; set; } = string.Empty;

        // ยอดที่บันทึกไว้ใน MonthlyOrderAction
        public decimal RecordedAmount { get; set; }

        // ค่าใช้จ่ายจริง โดย Skipped เท่ากับ 0
        public decimal ExpenseAmount { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
