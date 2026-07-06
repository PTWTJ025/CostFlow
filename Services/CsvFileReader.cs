using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CostFlow.Models;

namespace CostFlow.Services
{
    public class CsvFileReader
    {
        public ImportedFile ReadCsv(Stream stream, string fileName)
        {
            // Copy to memory stream to support reading multiple times for encoding and delimiter detection
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            var bytes = memoryStream.ToArray();

            // 1. Detect Encoding
            var encoding = DetectEncoding(bytes);

            // 2. Detect Delimiter
            char delimiter = DetectDelimiter(bytes, encoding);

            // 3. Read using CsvHelper
            var importedFile = new ImportedFile
            {
                FileName = fileName,
                FileType = "csv",
                SessionId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow
            };

            var importedSheet = new ImportedSheet
            {
                SheetName = Path.GetFileNameWithoutExtension(fileName),
                SheetIndex = 0,
                RawRows = new List<List<string>>()
            };

            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = delimiter.ToString(),
                HasHeaderRecord = false,
                BadDataFound = null,
                MissingFieldFound = null
            };

            using var readerMemoryStream = new MemoryStream(bytes);
            using var reader = new StreamReader(readerMemoryStream, encoding);
            using var csv = new CsvReader(reader, csvConfig);

            int maxColumns = 0;
            while (csv.Read())
            {
                var row = new List<string>();
                for (int i = 0; csv.TryGetField<string>(i, out var field); i++)
                {
                    row.Add(field ?? string.Empty);
                }
                importedSheet.RawRows.Add(row);
                if (row.Count > maxColumns)
                {
                    maxColumns = row.Count;
                }
            }

            importedSheet.RowCount = importedSheet.RawRows.Count;
            importedSheet.ColumnCount = maxColumns;
            importedFile.Sheets.Add(importedSheet);

            return importedFile;
        }

        private Encoding DetectEncoding(byte[] bytes)
        {
            try
            {
                // Verify if it is valid UTF-8
                var utf8Detector = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
                utf8Detector.GetString(bytes);
                return Encoding.UTF8;
            }
            catch (ArgumentException)
            {
                // Fallback to TIS-620/Windows-874 for Thai Excel CSV files
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(874);
            }
        }

        private char DetectDelimiter(byte[] bytes, Encoding encoding)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var reader = new StreamReader(ms, encoding);
                string firstLine = reader.ReadLine() ?? string.Empty;

                int commas = countOccurrences(firstLine, ',');
                int semicolons = countOccurrences(firstLine, ';');
                int tabs = countOccurrences(firstLine, '\t');

                if (semicolons > commas && semicolons > tabs) return ';';
                if (tabs > commas && tabs > semicolons) return '\t';
            }
            catch
            {
                // Fallback to standard comma
            }

            return ',';
        }

        private int countOccurrences(string text, char character)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == character) count++;
            }
            return count;
        }
    }
}
