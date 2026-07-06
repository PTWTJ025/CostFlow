using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using CostFlow.Models;

namespace CostFlow.Services
{
    public class ExcelFileReader
    {
        public ImportedFile ReadWorkbook(Stream stream, string fileName)
        {
            var importedFile = new ImportedFile
            {
                FileName = fileName,
                FileType = "xlsx",
                SessionId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow
            };

            // ClosedXML requires the stream to be seekable. Let's copy it to a MemoryStream to be safe.
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            memoryStream.Position = 0;

            using var workbook = new XLWorkbook(memoryStream);
            int sheetIndex = 0;

            foreach (var worksheet in workbook.Worksheets)
            {
                var importedSheet = new ImportedSheet
                {
                    SheetName = worksheet.Name,
                    SheetIndex = sheetIndex++,
                    RawRows = new List<List<string>>()
                };

                // Find the range of cells with data
                var lastRowUsed = worksheet.LastRowUsed();
                var lastColumnUsed = worksheet.LastColumnUsed();

                if (lastRowUsed == null || lastColumnUsed == null)
                {
                    // Empty sheet
                    importedSheet.RowCount = 0;
                    importedSheet.ColumnCount = 0;
                    importedFile.Sheets.Add(importedSheet);
                    continue;
                }

                int rowCount = lastRowUsed.RowNumber();
                int colCount = lastColumnUsed.ColumnNumber();

                importedSheet.RowCount = rowCount;
                importedSheet.ColumnCount = colCount;

                // Read all rows up to rowCount
                for (int r = 1; r <= rowCount; r++)
                {
                    var rowData = new List<string>();
                    var row = worksheet.Row(r);

                    for (int c = 1; c <= colCount; c++)
                    {
                        var cell = row.Cell(c);
                        rowData.Add(GetCellValueAsString(cell));
                    }
                    importedSheet.RawRows.Add(rowData);
                }

                importedFile.Sheets.Add(importedSheet);
            }

            return importedFile;
        }

        private string GetCellValueAsString(IXLCell cell)
        {
            if (cell == null || cell.IsEmpty())
            {
                return string.Empty;
            }

            try
            {
                var value = cell.Value;

                if (value.IsBlank)
                {
                    return string.Empty;
                }

                if (value.IsDateTime)
                {
                    // Check if it has time component or just date
                    var dt = value.GetDateTime();
                    return dt.TimeOfDay == TimeSpan.Zero 
                        ? dt.ToString("yyyy-MM-dd") 
                        : dt.ToString("yyyy-MM-dd HH:mm:ss");
                }

                if (value.IsNumber)
                {
                    return value.GetNumber().ToString();
                }

                if (value.IsBoolean)
                {
                    return value.GetBoolean() ? "True" : "False";
                }

                return value.ToString() ?? string.Empty;
            }
            catch
            {
                // Fallback to text representation in case of formula evaluation error
                return cell.Value.ToString() ?? string.Empty;
            }
        }
    }
}
