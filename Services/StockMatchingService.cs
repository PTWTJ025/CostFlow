using CostFlow.Data;
using CostFlow.Models;
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
        public string? DetectedActionVerb { get; set; }
    }

    public class StockMatchResult
    {
        public StockItem StockItem { get; set; } = null!;
        public int MatchPercentage { get; set; }
        public bool IsExactMapping { get; set; }
        public bool IsExactNameMatch { get; set; }
        public string? MatchReason { get; set; }
        public string? DetectedCategory { get; set; }
        public string? DetectedPosition { get; set; }
        public string? DetectedSpec { get; set; }
    }

    /// <summary>
    /// Search Engine Matching Engine (Okapi BM25 / TF-IDF)
    /// คำนวณน้ำหนักความสำคัญของแต่ละคำจาก "ความหายาก" (IDF - Inverse Document Frequency) ในคลังสต็อก:
    /// - คำที่โผล่บ่อยในเกือบทุกชิ้น (เช่น นิ้ว, ฟัน, ชุด, เครื่อง) ➜ น้ำหนักต่ำ
    /// - คำเฉพาะทางเทคนิค (เช่น ซุปเปอร์ลีน, สลัก, ผ่า, บู๊ช, เพลาขับด้าย, ท้ายเครื่อง) ➜ น้ำหนักสูง
    /// - ตัวเลขและรหัสเฉพาะ (เช่น 30, 34, 48, T37, T64, 2F1, 45mm) ➜ น้ำหนักสูงที่สุด
    /// จัดอันดับด้วยสูตรมาตรฐาน BM25 โดยไม่ต้องเขียน if-else เช็กสเปกทางวิศวกรรมให้เปราะบาง
    /// </summary>
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
            return Regex.Replace(input.ToLowerInvariant(), @"\s+", "");
        }

        /// <summary>
        /// สกัดข้อความสินค้า โดยตัดรหัสแผนก, ตัวเลขจำนวน, ว.ด.ป.เป้าหมาย และตัดคำกริยางานซ่อม/กลึงออก
        /// เพื่อให้ได้ชื่อชิ้นส่วนเนื้อแท้ทางวิศวกรรม
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

            // 1. ตัดรหัสแผนกนำหน้า เช่น "LCD00 ", "LBB00 ", "LAA00 ", "LCB00 "
            text = Regex.Replace(text, @"^[A-Z]{3}\d{2}\s*", "", RegexOptions.IgnoreCase);

            // 2. สกัดตัวเลขจำนวนและหน่วยนับ เช่น "จำนวน 2 ตัว", "2 ชิ้น", "10 ชุด"
            var qtyRegex = new Regex(@"(?:จำนวน|ยอด|รวม)?\s*(\d+(?:\.\d+)?)\s*(ตัว(?![ในนอกผู้เมียบนล่างที่])|ชิ้น|อัน|ชุด|ลูก|ม้วน|แผ่น|กล่อง|กก\.|กิโล|เมตร|เส้น|ม\.|ถุง|ถัง|กระป๋อง|ท่อ|ใบ|แกลลอน)", RegexOptions.IgnoreCase);
            var qtyMatch = qtyRegex.Match(text);
            if (qtyMatch.Success)
            {
                if (decimal.TryParse(qtyMatch.Groups[1].Value, out var q))
                {
                    info.DetectedQuantity = (int)Math.Round(q);
                }
                info.DetectedUnit = qtyMatch.Groups[2].Value;
                text = text.Remove(qtyMatch.Index, qtyMatch.Length);
            }

            // 3. ตัดข้อมูลวันเป้าหมาย/กำหนดส่ง เช่น "ต้องการ 20/8/69", "เป้าหมาย 1/5/69", "กำหนดส่ง 12/08/2026"
            var dateRegex = new Regex(@"(?:ต้องการ|เป้าหมาย|กำหนดส่ง|กำหนด|ส่งมอบ|ว\.ด\.ป\.|วันที่|นัดรับ|ภายใน)\s*:?\s*(\d{1,2}[\/\-\.]\d{1,2}[\/\-\.]\d{2,4})", RegexOptions.IgnoreCase);
            var dateMatch = dateRegex.Match(text);
            if (dateMatch.Success)
            {
                info.ExtractedTargetDate = dateMatch.Groups[1].Value;
                text = text.Remove(dateMatch.Index, dateMatch.Length);
            }

            // 4. ตัดคำกริยางานซ่อม/กลึง เพื่อให้ได้ชื่อชิ้นส่วนเนื้อแท้ เช่น "ซ่อมหัวเพลาขับด้าย" -> "เพลาขับด้าย"
            var verbRegex = new Regex(@"^(ซ่อมหัว|ซ่อม|คว้านรู|ดัดตั้งศูนย์|ดัด|ตั้งศูนย์|ไสร่องลิ่ม|ไส|เจียร|กลึง|ตัด|เชื่อม)\s*", RegexOptions.IgnoreCase);
            var verbMatch = verbRegex.Match(text);
            if (verbMatch.Success)
            {
                info.DetectedActionVerb = verbMatch.Groups[1].Value;
                text = text.Remove(verbMatch.Index, verbMatch.Length);
            }

            // 5. ตัด Administrative Noise / หมายเหตุอนุมัติ
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

            text = Regex.Replace(text, @"^(?:สั่งทำ|งานสั่งทำ|สั่งซื้อ|งานสั่งซื้อ)\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[\s,;\-]+$", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            info.CleanName = string.IsNullOrWhiteSpace(text) ? rawText.Trim() : text;
            return info;
        }

        /// <summary>
        /// ตัวตัดคำสำหรับภาษาไทยเชิงช่าง ตัวเลข ขนาดมิล รหัสรุ่น และสเปกทางวิศวกรรม
        /// </summary>
        public static List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            // 1. แก้ไขคำสะกดผิดที่พบบ่อยในระบบให้เป็นรูปมาตรฐาน
            string s = text.Trim().ToLowerInvariant();
            s = s.Replace("ต้ว", "ตัว");
            s = s.Replace("เฟีอง", "เฟือง");
            s = s.Replace("เฟิอง", "เฟือง");
            s = s.Replace("ชุเปอร์ลีน", "ซุปเปอร์ลีน");
            s = s.Replace("ชุบเปอร์ลีน", "ซุปเปอร์ลีน");
            s = s.Replace("ตุ๊กเพลา", "ตุ๊กตาเพลา");
            s = s.Replace("แกบพูลเลย์", "แกนพูลเลย์");
            s = s.Replace("แกนพลูเลย์", "แกนพูลเลย์");
            s = s.Replace("พลูเล่ย์", "พูลเลย์");
            s = s.Replace("พูลเล่ย์", "พูลเลย์");
            s = s.Replace("2for1", "2f1");
            s = s.Replace("ด้น", "ด้าน");
            s = s.Replace("เครือง", "เครื่อง");
            s = s.Replace("วงแหว ", "วงแหวน ");

            // 2. แปลงเศษส่วนและขนาดนิ้วให้อยู่ในรูปเดียวกัน: 5"1/2, 5 1/2", 5.5" -> 5.5นิ้ว
            s = Regex.Replace(s, @"(\d+)\s*[""”]\s*1/2", "$1.5นิ้ว");
            s = Regex.Replace(s, @"(\d+)\s*1/2\s*[""”]", "$1.5นิ้ว");
            s = Regex.Replace(s, @"(\d+)\s*[""”]", "$1นิ้ว");

            // 3. แปลงมิลลิเมตรให้อยู่ในรูปเดียวกัน: 30 mm, 30มม, 30 มิล -> 30mm
            s = Regex.Replace(s, @"(\d+)\s*(?:มม|มิล|mm)\b", "$1mm");

            // 4. กระจายรหัสรุ่นช่วง: T22-23 -> T22 T23, T37-38 -> T37 T38
            s = Regex.Replace(s, @"\bT(\d+)-(\d+)\b", m =>
            {
                if (int.TryParse(m.Groups[1].Value, out int start) && int.TryParse(m.Groups[2].Value, out int end) && end > start && end - start <= 10)
                {
                    var list = new List<string>();
                    for (int k = start; k <= end; k++) list.Add($"t{k}");
                    return string.Join(" ", list);
                }
                return m.Value;
            });

            // 5. กระจายรหัสรุ่นคั่นจุลภาค: T11,15,43-45
            s = Regex.Replace(s, @"\bT(\d+)(?:,\s*(\d+))+", m =>
            {
                var matches = Regex.Matches(m.Value, @"\d+");
                return string.Join(" ", matches.Select(x => $"t{x.Value}"));
            });

            var tokens = new List<string>();

            // พจนานุกรมคำศัพท์ชิ้นส่วนหลักในคลังสต็อก (Compound Terms)
            string[] components = new[]
            {
                "ฝาปิดน้ำมัน", "ฝาปิด", "น้ำมัน",
                "ตัวครอบวงแหวน", "ตัวครอบ", "วงแหวน",
                "เหล็กประกบ",
                "แกนพูลเลย์", "พูลเลย์สายพาน", "พูลเลย์", "สายพาน",
                "แกนล้อดึงสายพาน", "ล้อดึงสายพาน", "ล้อดึง",
                "แกนล้อตึงสายพาน", "ล้อตึงสายพาน", "ล้อตึง",
                "สลักล้อดึงสายพาน", "สลัก",
                "ลูกกลิ้งทับใย", "ลูกกลิ้ง", "ทับใย",
                "แกนเฟือง",
                "เกลียวนอก", "เกลียวใน",
                "หัวเครื่อง", "กลางเครื่อง", "ท้ายเครื่อง", "หน้าเครื่อง",
                "ชุดบน", "ชุดล่าง",
                "มีหน้าแปลน", "ไม่มีหน้าแปลน", "หน้าแปลน",
                "แบบผ่า",
                "เฟืองท้ายเพลาขับด้าย", "เฟืองท้าย", "เพลาขับด้าย", "ขับด้าย",
                "เฟืองเพลาขับด้าย", "เฟืองขับเพลาล่าง", "เพลาล่าง", "เพลาลูกกลิ้ง",
                "ซุปเปอร์ลีน",
                "ตุ๊กตาเฟือง", "ตุ๊กตาเพลาล่าง", "ตุ๊กตาโยกเฟือง", "ตุ๊กตาเพลาขับด้าย",
                "ตุ๊กตาล้อตึงสายพาน", "ตุ๊กตา",
                "โยกเฟือง", "ด้าน l", "ด้าน r", "ด้านl", "ด้านr",
                "ขนาดเล็ก", "ขนาดใหญ่",
                "สั่งจีน", "สต๊อกคลัง",
                "เฟืองขับ", "เฟือง", "เพลา", "ฟัน", "นิ้ว"
            };

            foreach (var comp in components)
            {
                if (s.Contains(comp))
                {
                    tokens.Add(comp.Replace(" ", ""));
                }
            }

            // สกัดตัวเลข, รหัสโมเดล, ขนาดมิติ และรหัสผสม (เช่น 2F1, T37, 45mm, 48, 30)
            var specMatches = Regex.Matches(s, @"(?:\d+[a-z]+\d*|[a-z]+\d+[a-z0-9]*|\d+(?:\.\d+)?(?:mm|นิ้ว)?|\d+|[a-z]{1,4})", RegexOptions.IgnoreCase);
            foreach (Match m in specMatches)
            {
                var val = m.Value.ToLowerInvariant();
                tokens.Add(val);

                // ถ้ามีหน่วย mm หรือ นิ้ว ให้เพิ่มตัวเลขโดดๆ ด้วย เพื่อความเข้ากันได้
                if (val.EndsWith("mm") || val.EndsWith("นิ้ว"))
                {
                    var numOnly = Regex.Match(val, @"^\d+(?:\.\d+)?");
                    if (numOnly.Success) tokens.Add(numOnly.Value);
                }
            }

            return tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        }

        /// <summary>
        /// ค้นหาอะไหล่สต็อกด้วยระบบ Okapi BM25 / TF-IDF Search Engine
        /// </summary>
        public async Task<List<StockMatchResult>> FindTopMatchesAsync(string searchName, int topN = 5)
        {
            var results = new List<StockMatchResult>();
            if (string.IsNullOrWhiteSpace(searchName))
                return results;

            var cleanInfo = CleanOrderDescription(searchName);
            var cleanText = cleanInfo.CleanName;
            var normalizedSearch = NormalizeText(cleanText);

            var allStockItems = await _context.StockItems.ToListAsync();

            // 1. ตรวจสอบการจำคำศัพท์จาก ItemMappings (Confirmed Mapping)
            var existingMapping = await _context.ItemMappings
                .FirstOrDefaultAsync(m => m.OrderName == cleanText || m.OrderName == searchName.Trim());

            if (existingMapping != null && !string.IsNullOrEmpty(existingMapping.StockItemCode))
            {
                var mappedItem = allStockItems.FirstOrDefault(s => s.ProductCode == existingMapping.StockItemCode);
                if (mappedItem != null)
                {
                    results.Add(new StockMatchResult
                    {
                        StockItem = mappedItem,
                        MatchPercentage = 100,
                        IsExactMapping = true,
                        IsExactNameMatch = false,
                        MatchReason = "ตรงกับหน่วยความจำคำศัพท์ (ItemMappings) 100%",
                        DetectedCategory = mappedItem.Category
                    });
                    return results;
                }
            }

            // 2. ตรวจสอบชื่อตรงเป๊ะ 100% หรือตรงกับรหัสสินค้า
            var exactStockItem = allStockItems.FirstOrDefault(s =>
                (!string.IsNullOrEmpty(s.ProductName) && NormalizeText(s.ProductName) == normalizedSearch) ||
                (!string.IsNullOrEmpty(s.ProductCode) && s.ProductCode.Equals(searchName.Trim(), StringComparison.OrdinalIgnoreCase)));

            if (exactStockItem != null)
            {
                results.Add(new StockMatchResult
                {
                    StockItem = exactStockItem,
                    MatchPercentage = 100,
                    IsExactMapping = false,
                    IsExactNameMatch = true,
                    MatchReason = "ชื่อชิ้นส่วนหรือรหัสสินค้าตรงกัน 100% เป๊ะ",
                    DetectedCategory = exactStockItem.Category
                });
                return results;
            }

            // 3. เตรียม Corpus BM25 จาก StockItems ทั้งหมด
            var docTokensList = new List<List<string>>();
            foreach (var item in allStockItems)
            {
                string fullDocText = $"{item.ProductName} {item.StockGroup} {item.Category} {item.ProductCode}";
                docTokensList.Add(Tokenize(fullDocText));
            }

            int N = allStockItems.Count;
            if (N == 0) return results;

            double avgDocLength = docTokensList.Average(d => d.Count);
            if (avgDocLength <= 0) avgDocLength = 1.0;

            // คำนวณ Document Frequency (DF)
            var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in docTokensList)
            {
                var uniqueTerms = new HashSet<string>(doc, StringComparer.OrdinalIgnoreCase);
                foreach (var term in uniqueTerms)
                {
                    df[term] = df.GetValueOrDefault(term, 0) + 1;
                }
            }

            // คำนวณ Okapi BM25 Inverse Document Frequency (IDF)
            var idfMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (term, count) in df)
            {
                double idfVal = Math.Log(1.0 + (N - count + 0.5) / (count + 0.5));
                idfMap[term] = Math.Max(0.1, idfVal);
            }

            // 4. Tokenize คำค้นหา (Query) และคำนวณคะแนน BM25
            var queryTokens = Tokenize(cleanText);
            if (queryTokens.Count == 0) return results;

            // ผลรวม IDF ของคำค้นหา สำหรับคำนวณ Query Coverage Ratio
            double totalQueryIdf = 0.0;
            foreach (var term in queryTokens)
            {
                double idf = idfMap.GetValueOrDefault(term, Math.Log(1.0 + (N + 0.5) / 1.5));
                double boost = 1.0;
                if (Regex.IsMatch(term, @"^\d+$")) boost = 1.5;
                else if (Regex.IsMatch(term, @"^[a-z]\d+|\d+[a-z]+|\d+mm|\d+นิ้ว", RegexOptions.IgnoreCase)) boost = 1.8;

                totalQueryIdf += idf * boost;
            }
            if (totalQueryIdf <= 0) totalQueryIdf = 1.0;

            const double k1 = 1.2;
            const double b = 0.75;
            var candidates = new List<(StockItem Item, double BM25Score, int Percentage, string Reason, string? Spec)>();

            for (int i = 0; i < allStockItems.Count; i++)
            {
                var item = allStockItems[i];
                var docTokens = docTokensList[i];
                int docLen = docTokens.Count;

                var dtf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in docTokens)
                    dtf[t] = dtf.GetValueOrDefault(t, 0) + 1;

                double bm25 = 0.0;
                double matchedQueryIdf = 0.0;
                var matchedTerms = new List<(string Term, double Idf)>();

                foreach (var term in queryTokens)
                {
                    if (dtf.TryGetValue(term, out int tf))
                    {
                        double idf = idfMap.GetValueOrDefault(term, 0.1);
                        double boost = 1.0;
                        if (Regex.IsMatch(term, @"^\d+$")) boost = 1.5;
                        else if (Regex.IsMatch(term, @"^[a-z]\d+|\d+[a-z]+|\d+mm|\d+นิ้ว", RegexOptions.IgnoreCase)) boost = 1.8;

                        double num = tf * (k1 + 1.0);
                        double denom = tf + k1 * (1.0 - b + b * (docLen / avgDocLength));
                        double termScore = idf * boost * (num / denom);

                        bm25 += termScore;
                        matchedQueryIdf += idf * boost;
                        matchedTerms.Add((term, idf));
                    }
                }

                if (bm25 > 0)
                {
                    double coverage = matchedQueryIdf / totalQueryIdf;
                    double precision = (double)matchedTerms.Count / Math.Max(1, docLen);
                    double pctRaw = (coverage * 80.0) + (precision * 20.0);
                    if (coverage >= 0.95) pctRaw = Math.Max(pctRaw, 95.0);
                    int percentage = Math.Clamp((int)Math.Round(pctRaw), 15, 100);

                    // สร้างคำอธิบายเหตุผล (Reason) ที่โปร่งใส เข้าใจง่าย
                    var highTerms = matchedTerms
                        .Where(t => t.Idf >= 2.5 || Regex.IsMatch(t.Term, @"^\d+$|^[a-z]\d+|\d+mm|\d+นิ้ว", RegexOptions.IgnoreCase))
                        .Select(t => t.Term)
                        .Distinct()
                        .ToList();

                    var medTerms = matchedTerms
                        .Where(t => t.Idf >= 1.5 && t.Idf < 2.5 && !highTerms.Contains(t.Term))
                        .Select(t => t.Term)
                        .Distinct()
                        .ToList();

                    var reasons = new List<string>();
                    if (highTerms.Any()) reasons.Add($"ตรงคำเฉพาะ/รหัส: {string.Join(", ", highTerms)}");
                    if (medTerms.Any()) reasons.Add($"ตรงคำศัพท์: {string.Join(", ", medTerms)}");
                    if (!reasons.Any()) reasons.Add($"ตรงคำทั่วไป: {string.Join(", ", matchedTerms.Select(t => t.Term).Distinct())}");

                    string reasonStr = string.Join(" • ", reasons) + $" (BM25: {bm25:F1})";
                    string? detectedSpec = highTerms.FirstOrDefault(t => Regex.IsMatch(t, @"\d+|t\d+|mm", RegexOptions.IgnoreCase));

                    candidates.Add((item, bm25, percentage, reasonStr, detectedSpec));
                }
            }

            // จัดเรียงตามคะแนน BM25 จากมากไปน้อย
            var sorted = candidates
                .OrderByDescending(c => c.BM25Score)
                .ThenBy(c => c.Item.ProductName?.Length ?? 0)
                .Take(topN)
                .Select(c => new StockMatchResult
                {
                    StockItem = c.Item,
                    MatchPercentage = c.Percentage,
                    IsExactMapping = false,
                    IsExactNameMatch = NormalizeText(c.Item.ProductName ?? "") == normalizedSearch,
                    MatchReason = c.Reason,
                    DetectedCategory = c.Item.StockGroup ?? c.Item.Category,
                    DetectedSpec = c.Spec
                })
                .ToList();

            return sorted;
        }

        public async Task<(CleanedOrderInfo CleanInfo, List<StockMatchResult> Matches)> CleanAndFindMatchesAsync(string rawText, int topN = 5)
        {
            var cleanInfo = CleanOrderDescription(rawText);
            var matches = await FindTopMatchesAsync(cleanInfo.CleanName, topN);

            return (cleanInfo, matches);
        }
    }
}
