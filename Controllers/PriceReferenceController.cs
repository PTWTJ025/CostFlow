using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using CostFlow.Data;
using CostFlow.Models;
using System.Linq;
using System.Threading.Tasks;

namespace CostFlow.Controllers
{
    [Authorize]
    public class PriceReferenceController : Controller
    {
        private readonly AppDbContext _context;

        public PriceReferenceController(AppDbContext context)
        {
            _context = context;
        }

        // GET: /PriceReference
        public async Task<IActionResult> Index(string search, int page = 1)
        {
            const int pageSize = 50;
            var query = _context.ProductPrices.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var cleanSearch = search.Trim().ToLower();
                query = query.Where(p => p.ProductCode.ToLower().Contains(cleanSearch) || 
                                         p.ProductName.ToLower().Contains(cleanSearch));
            }

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(p => p.ProductCode)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewData["Search"] = search;
            ViewData["Page"] = page;
            ViewData["TotalPages"] = (totalCount + pageSize - 1) / pageSize;
            ViewData["TotalCount"] = totalCount;

            return View(items);
        }

        // GET: /PriceReference/SearchApi
        [HttpGet]
        public async Task<IActionResult> SearchApi(string search, int page = 1)
        {
            const int pageSize = 50;
            var query = _context.ProductPrices.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var cleanSearch = search.Trim().ToLower();
                query = query.Where(p => p.ProductCode.ToLower().Contains(cleanSearch) || 
                                         p.ProductName.ToLower().Contains(cleanSearch));
            }

            int totalCount = await query.CountAsync();
            var items = await query
                .OrderBy(p => p.ProductCode)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Json(new {
                items = items,
                currentPage = page,
                totalPages = (totalCount + pageSize - 1) / pageSize,
                totalCount = totalCount
            });
        }

        // POST: /PriceReference/Create
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Create([FromBody] ProductPrice model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลรหัสสินค้าไม่ถูกต้อง" });
            }

            var code = model.ProductCode.Trim();
            var existing = await _context.ProductPrices.FindAsync(code);
            if (existing != null)
            {
                return Json(new { success = false, error = "รหัสสินค้านี้มีอยู่ในระบบแล้ว" });
            }

            var price = new ProductPrice
            {
                ProductCode = code,
                ProductName = model.ProductName?.Trim() ?? string.Empty,
                Unit = model.Unit?.Trim() ?? string.Empty,
                PricePerUnit = model.PricePerUnit,
                TotalQty = model.TotalQty,
                TotalValue = model.TotalValue,
                Sources = model.Sources?.Trim() ?? "งานคีย์ระบบ"
            };

            _context.ProductPrices.Add(price);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/Edit
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit([FromBody] ProductPrice model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.ProductCode))
            {
                return Json(new { success = false, error = "ข้อมูลรหัสสินค้าไม่ถูกต้อง" });
            }

            var code = model.ProductCode.Trim();
            var existing = await _context.ProductPrices.FindAsync(code);
            if (existing == null)
            {
                return Json(new { success = false, error = "ไม่พบรหัสสินค้าในระบบ" });
            }

            existing.ProductName = model.ProductName?.Trim() ?? string.Empty;
            existing.Unit = model.Unit?.Trim() ?? string.Empty;
            existing.PricePerUnit = model.PricePerUnit;
            existing.TotalQty = model.TotalQty;
            existing.TotalValue = model.TotalValue;
            if (!string.IsNullOrWhiteSpace(model.Sources))
            {
                existing.Sources = model.Sources.Trim();
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // POST: /PriceReference/Delete
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                return Json(new { success = false, error = "รหัสสินค้าไม่ถูกต้อง" });
            }

            var code = productCode.Trim();
            var existing = await _context.ProductPrices.FindAsync(code);
            if (existing == null)
            {
                return Json(new { success = false, error = "ไม่พบรหัสสินค้าในระบบ" });
            }

            _context.ProductPrices.Remove(existing);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }
    }
}
