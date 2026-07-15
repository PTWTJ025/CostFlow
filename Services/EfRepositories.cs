using System.Linq;
using System.Threading.Tasks;
using CostFlow.Data;
using CostFlow.Models;
using Microsoft.EntityFrameworkCore;

namespace CostFlow.Services
{
    public class ProductPriceRepository : IProductPriceRepository
    {
        private readonly AppDbContext _context;

        public ProductPriceRepository(AppDbContext context)
        {
            _context = context;
        }

        public IQueryable<ProductPrice> Query()
        {
            return _context.ProductPrices.AsQueryable();
        }

        public async Task AddAsync(ProductPrice entity)
        {
            await _context.ProductPrices.AddAsync(entity);
        }

        public Task DeleteAsync(ProductPrice entity)
        {
            _context.ProductPrices.Remove(entity);
            return Task.CompletedTask;
        }

        public async Task SaveChangesAsync()
        {
            await _context.SaveChangesAsync();
        }
    }
}
