using System;
using System.Collections.Generic;

namespace CostFlow.Models
{
    public class ImportedFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty; // xlsx / csv
        public Guid SessionId { get; set; }
        public DateTime UploadedAt { get; set; }
        public List<ImportedSheet> Sheets { get; set; } = new();
    }

    public class ImportedSheet
    {
        public string SheetName { get; set; } = string.Empty;
        public int SheetIndex { get; set; }
        public int RowCount { get; set; }
        public int ColumnCount { get; set; }
        public List<List<string>> RawRows { get; set; } = new();
    }
}
