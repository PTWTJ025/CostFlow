using System.Threading.Tasks;
using CostFlow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CostFlow.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        private readonly INotificationService _notificationService;

        public NotificationController(INotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        [HttpGet]
        public async Task<IActionResult> GetNotifications([FromQuery] int limit = 30)
        {
            var result = await _notificationService.GetNotificationsAsync(limit);
            return Ok(new
            {
                success = true,
                unreadCount = result.UnreadCount,
                items = result.Items
            });
        }

        [HttpPost("{id}/read")]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            var success = await _notificationService.MarkAsReadAsync(id);
            var unreadCount = await _notificationService.GetUnreadCountAsync();
            return Ok(new { success, unreadCount });
        }

        [HttpPost("read-all")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var success = await _notificationService.MarkAllAsReadAsync();
            return Ok(new { success, unreadCount = 0 });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var success = await _notificationService.DeleteAsync(id);
            var unreadCount = await _notificationService.GetUnreadCountAsync();
            return Ok(new { success, unreadCount });
        }
    }
}
