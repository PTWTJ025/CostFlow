namespace CostFlow.Models
{
    public class SettingsViewModel
    {
        public int RetentionMonths { get; set; } = 24;
        public string ArchiveAppScriptUrl { get; set; } = string.Empty;
        public string SpreadsheetUrl { get; set; } = string.Empty;
        public string ArchiveSheetUrl { get; set; } = string.Empty;
        public string CutoffDateDisplay { get; set; } = string.Empty;
        public int EligiblePlanCount { get; set; }
        public int EligibleOrderCount { get; set; }
    }
}
