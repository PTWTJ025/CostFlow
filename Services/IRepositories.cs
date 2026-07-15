using System.Linq;
using System.Threading.Tasks;
using CostFlow.Models;

namespace CostFlow.Services
{
    public interface IProductPriceRepository
    {
        IQueryable<ProductPrice> Query();
        Task AddAsync(ProductPrice entity);
        Task DeleteAsync(ProductPrice entity);
        Task SaveChangesAsync();
    }
}
