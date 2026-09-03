using Microsoft.AspNetCore.Mvc;

namespace CostFlow.Controllers
{
    public class StockController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
