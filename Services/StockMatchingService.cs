using CostFlow.Data;
using CostFlow.Models;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace CostFlow.Services
{
    public class CleanedOrderInfo
    {
        public string RawText { get; set; } = string.Empty;
        public string CleanName { get; set; } = string.Empty;
        public int DetectedQuantity { get; set; } = 1;
        public string? DetectedUnit { get; set; }
        public string? ExtractedTargetDate { get; set; }
        public string? ExtractedRemarks { get; set; }
    }

    public class StockMatchResult
    {
        public StockItem StockItem { get; set; } = null!;
        public int MatchPercentage { get; set; }
        public bool IsExactMapping { get; set; }
        public bool IsExactNameMatch { get; set; }
    }

    public class StockMatchingService
    {
        private readonly AppDbContext _context;

        public StockMatchingService(AppDbContext context)
        {
            _context = context;
        }

        public string NormalizeText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            // ลบช่องว่างทั้งหมด และทำเป็นตัวพิมพ์เล็ก (เผื่อมีภาษาอังกฤษ)
            return Regex.Replace(input.ToLowerInvariant(), @"\s+", "");
        }

        /// <summary>
        /// สกัดข้อความสินค้า โดยตัดตัวเลขจำนวน, ว.ด.ป.เป้าหมาย และหมายเหตุงานธุรการ/อนุมัติออก เพื่อให้เหลือเฉพาะชื่อสินค้าและสเปกช่าง
        /// </summary>
        public CleanedOrderInfo CleanOrderDescription(string rawText)
        {
            var info = new CleanedOrderInfo
            {
                RawText = rawText ?? string.Empty,
                CleanName = rawText ?? string.Empty,
                DetectedQuantity = 1
            };

            if (string.IsNullOrWhiteSpace(rawText))
                return info;

            string text = rawText.Trim();

            // 1. สกัดตัวเลขจำนวนและหน่วยนับ เช่น "จำนวน 2 ตัว", "2 ชิ้น", "10 ชุด"
            var qtyRegex = new Regex(@"(?:จำนวน|ยอด|รวม)?\s*(\d+(?:\.\d+)?)\s*(ตัว|ชิ้น|อัน|ชุด|ลูก|ม้วน|แผ่น|กล่อง|กก\.|กิโล|เมตร|เส้น|ม\.|ถุง|ถัง|กระป๋อง|ท่อ|ใบ|แกลลอน)", RegexOptions.IgnoreCase);
            var qtyMatch = qtyRegex.Match(text);
            if (qtyMatch.Success)
            {
                if (decimal.TryParse(qtyMatch.Groups[1].Value, out var q))
                {
                    info.DetectedQuantity = (int)Math.Round(q);
                }
                info.DetectedUnit = qtyMatch.Groups[2].Value;
                // ตัดส่วนจำนวนออกจากข้อความ
                text = text.Remove(qtyMatch.Index, qtyMatch.Length);
            }

            // 2. ตัดข้อมูลวันเป้าหมาย/กำหนดส่ง เช่น "เป้าหมาย 1/5/69", "กำหนดส่ง 12/08/2026"
            var dateRegex = new Regex(@"(?:เป้าหมาย|กำหนดส่ง|กำหนด|ส่งมอบ|ว\.ด\.ป\.|วันที่|นัดรับ|ภายใน)\s*:?\s*(\d{1,2}[\/\-\.]\d{1,2}[\/\-\.]\d{2,4})", RegexOptions.IgnoreCase);
            var dateMatch = dateRegex.Match(text);
            if (dateMatch.Success)
            {
                info.ExtractedTargetDate = dateMatch.Groups[1].Value;
                text = text.Remove(dateMatch.Index, dateMatch.Length);
            }

            // 3. ตัด Administrative Noise / หมายเหตุอนุมัติ / กระบวนการทำงาน (ตัดตั้งแต่คำที่ตรวจพบเป็นต้นไป)
            string[] adminTriggers = new[]
            {
                "แก้ไขใหม่", "กรณีไม่อนุมัติ", "กรณีอนุมัติ", "รออนุมัติ", "ฝ่ายผลิต",
                "ไม่อนุมัติจาก", "งานเร่งด่วน", "ยกเลิก", "งานสั่งทำ", "สั่งทำ", "สั่งซื้อด่วน"
            };

            foreach (var trigger in adminTriggers)
            {
                int idx = text.IndexOf(trigger, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    info.ExtractedRemarks = (info.ExtractedRemarks != null ? info.ExtractedRemarks + " | " : "") + text.Substring(idx).Trim();
                    text = text.Substring(0, idx);
                }
            }

            // 4. ตัดคำขึ้นต้นที่ไม่ใช่ชื่อสินค้า เช่น "สั่งทำ ", "งานสั่งทำ "
            text = Regex.Replace(text, @"^(?:สั่งทำ|งานสั่งทำ|สั่งซื้อ|งานสั่งซื้อ)\s*", "", RegexOptions.IgnoreCase);

            // 5. ทำความสะอาดเครื่องหมายวรรคตอนส่วนเกินที่อาจค้างอยู่ท้ายประโยค
            text = Regex.Replace(text, @"[\s,;\-]+$", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            info.CleanName = string.IsNullOrWhiteSpace(text) ? rawText.Trim() : text;
            return info;
        }

        public async Task<List<StockMatchResult>> FindTopMatchesAsync(string searchName, int topN = 5)
        {
            var results = new List<StockMatchResult>();
            if (string.IsNullOrWhiteSpace(searchName))
                return results;

            var normalizedSearch = NormalizeText(searchName);

            // 1. ตรวจสอบใน ItemMappings ก่อน (Exact Memory Mapping)
            var mappings = await _context.ItemMappings.ToListAsync();
            var exactMapping = mappings.FirstOrDefault(m => NormalizeText(m.OrderName) == normalizedSearch);

            if (exactMapping != null)
            {
                var stockItem = await _context.StockItems.FirstOrDefaultAsync(s => s.ProductCode == exactMapping.StockItemCode);
                if (stockItem != null)
                {
                    results.Add(new StockMatchResult
                    {
                        StockItem = stockItem,
                        MatchPercentage = 100,
                        IsExactMapping = true,
                        IsExactNameMatch = true
                    });
                    return results;
                }
            }

            // 2. ตรวจสอบว่าตรงกับ StockItems ในฐานข้อมูลแบบ 100% ตัวอักษรเป๊ะๆ หรือไม่
            var allStockItems = await _context.StockItems.ToListAsync();
            var exactStockItem = allStockItems.FirstOrDefault(s =>
                NormalizeText(s.ProductName) == normalizedSearch ||
                s.ProductCode.Equals(searchName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (exactStockItem != null)
            {
                results.Add(new StockMatchResult
                {
                    StockItem = exactStockItem,
                    MatchPercentage = 100,
                    IsExactMapping = false,
                    IsExactNameMatch = true
                });
                return results;
            }

            // 3. ใช้ FuzzySharp คำนวณความเหมือน (TokenSetRatio และ WeightedRatio)
            var fuzzyMatches = allStockItems
                .Select(item =>
                {
                    var normName = NormalizeText(item.ProductName);
                    int tokenScore = Fuzz.TokenSetRatio(normalizedSearch, normName);
                    int partialScore = Fuzz.PartialRatio(normalizedSearch, normName);
                    int bestScore = Math.Max(tokenScore, partialScore);

                    // ถ้าชื่อตัวอักษรเหมือนกันหมดแต่สลับวรรคตอน ให้ถือเป็น 100
                    bool isExact = normName == normalizedSearch;
                    if (isExact) bestScore = 100;

                    return new
                    {
                        Item = item,
                        Score = bestScore,
                        IsExact = isExact
                    };
                })
                .Where(x => x.Score >= 30) // เอาเฉพาะที่มีความใกล้เคียงอย่างน้อย 30%
                .OrderByDescending(x => x.Score)
                .Take(topN)
                .Select(x => new StockMatchResult
                {
                    StockItem = x.Item,
                    MatchPercentage = x.Score,
                    IsExactMapping = false,
                    IsExactNameMatch = x.IsExact
                })
                .ToList();

            return fuzzyMatches;
        }

        public async Task<(CleanedOrderInfo CleanInfo, List<StockMatchResult> Matches)> CleanAndFindMatchesAsync(string rawText, int topN = 5)
        {
            var cleanInfo = CleanOrderDescription(rawText);
            var matches = await FindTopMatchesAsync(cleanInfo.CleanName, topN);

            // ถ้าหาด้วย CleanName แล้วยังได้ผลน้อย ลองเช็คด้วย RawText เสริม
            if (!matches.Any() && cleanInfo.CleanName != rawText)
            {
                matches = await FindTopMatchesAsync(rawText, topN);
            }

            return (cleanInfo, matches);
        }
    }
}
