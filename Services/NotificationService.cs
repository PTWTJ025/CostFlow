using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CostFlow.Data;
using CostFlow.Hubs;
using CostFlow.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CostFlow.Services
{
    public interface INotificationService
    {
        Task<NotificationListResult> GetNotificationsAsync(int limit = 30, bool isStaff = false);
        Task<AppNotification> CreateNotificationAsync(
            string type,
            string title,
            string message,
            string? targetUrl,
            string? category = null,
            string? referenceId = null,
            string? actionUser = null,
            bool isRead = false);
        Task<bool> MarkAsReadAsync(int id);
        Task<bool> MarkAllAsReadAsync();
        Task<bool> DeleteAsync(int id);
        Task<int> GetUnreadCountAsync(bool isStaff = false);
    }

    public class NotificationDto
    {
        public int Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? TargetUrl { get; set; }
        public bool IsRead { get; set; }
        public string? Category { get; set; }
        public string? ReferenceId { get; set; }
        public string? ActionUser { get; set; }
        public string TimeAgo { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class NotificationListResult
    {
        public int UnreadCount { get; set; }
        public List<NotificationDto> Items { get; set; } = new();
    }

    public class NotificationService : INotificationService
    {
        private readonly AppDbContext _db;
        private readonly IHubContext<DashboardHub> _hubContext;
        private readonly IDateTimeProvider _dateTimeProvider;

        public NotificationService(
            AppDbContext db,
            IHubContext<DashboardHub> hubContext,
            IDateTimeProvider dateTimeProvider)
        {
            _db = db;
            _hubContext = hubContext;
            _dateTimeProvider = dateTimeProvider;
        }

        public async Task<int> GetUnreadCountAsync(bool isStaff = false)
        {
            var query = _db.Notifications.Where(n => !n.IsRead && n.Type != "STOCK_UPDATE");
            if (isStaff)
            {
                query = query.Where(n => n.Type == "ORDER_TRACKING" || 
                                         n.Type == "ORDER_RECEIVE" || 
                                         n.Type == "STOCK_RECEIVE" || 
                                         n.Type == "STOCK_CREATE");
            }
            else
            {
                query = query.Where(n => n.Type == "ORDER_TRACKING" || 
                                         n.Type == "ORDER_RECEIVE" || 
                                         n.Type == "STOCK_RECEIVE" || 
                                         n.Type == "STOCK_CREATE" || 
                                         n.Type == "PRODUCTION_PLAN" || 
                                         n.Type == "MONTHLY_COST");
            }
            return await query.CountAsync();
        }

        public async Task<NotificationListResult> GetNotificationsAsync(int limit = 30, bool isStaff = false)
        {
            var query = _db.Notifications.AsNoTracking().Where(n => n.Type != "STOCK_UPDATE");

            if (isStaff)
            {
                // ช่างหน้างาน (Staff): เห็นเฉพาะ 4 เหตุการณ์สำคัญ
                // 1. คีย์สั่งซื้อหน้างาน (ORDER_TRACKING)
                // 2. ติดตามรับสินค้าหน้างาน (ORDER_RECEIVE)
                // 3. บันทึกของลงสต็อก (STOCK_RECEIVE)
                // 4. เพิ่มสต็อกใหม่ (STOCK_CREATE)
                query = query.Where(n => n.Type == "ORDER_TRACKING" || 
                                         n.Type == "ORDER_RECEIVE" || 
                                         n.Type == "STOCK_RECEIVE" || 
                                         n.Type == "STOCK_CREATE");
            }
            else
            {
                // หัวหน้างาน (Admin/Supervisor): เห็น 4 รายการนี้ + อัปโหลดแผนสั่งผลิต (PRODUCTION_PLAN) + แจ้งเตือนรอบบัญชี (MONTHLY_COST)
                query = query.Where(n => n.Type == "ORDER_TRACKING" || 
                                         n.Type == "ORDER_RECEIVE" || 
                                         n.Type == "STOCK_RECEIVE" || 
                                         n.Type == "STOCK_CREATE" || 
                                         n.Type == "PRODUCTION_PLAN" || 
                                         n.Type == "MONTHLY_COST");
            }

            var notifs = await query
                .OrderByDescending(n => n.CreatedAt)
                .Take(limit)
                .ToListAsync();

            var unreadCount = await query.CountAsync(n => !n.IsRead);
            var now = _dateTimeProvider.Now;

            var dtoList = notifs.Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = (n.Title == "สั่งซื้อสินค้าทั่วไป") ? n.Title : n.Title,
                Message = n.Message,
                TargetUrl = (n.TargetUrl == "/MonthlyCost") ? "/MonthlyCost/Detail/กันยายน 2569" : (n.TargetUrl ?? string.Empty),
                IsRead = n.IsRead,
                Category = n.Category,
                ReferenceId = n.ReferenceId,
                ActionUser = n.ActionUser,
                TimeAgo = FormatThaiTimeAgo(n.CreatedAt, now),
                CreatedAt = n.CreatedAt
            }).ToList();

            return new NotificationListResult
            {
                UnreadCount = unreadCount,
                Items = dtoList
            };
        }

        public async Task<AppNotification> CreateNotificationAsync(
            string type,
            string title,
            string message,
            string? targetUrl,
            string? category = null,
            string? referenceId = null,
            string? actionUser = null,
            bool isRead = false)
        {
            var notif = new AppNotification
            {
                Type = type,
                Title = title,
                Message = message,
                TargetUrl = targetUrl,
                Category = category,
                ReferenceId = referenceId,
                ActionUser = actionUser,
                IsRead = isRead,
                CreatedAt = DateTime.UtcNow
            };

            _db.Notifications.Add(notif);
            await _db.SaveChangesAsync();

            // Broadcast via SignalR
            var now = _dateTimeProvider.Now;
            var unreadCount = await _db.Notifications.CountAsync(n => !n.IsRead);

            var dto = new NotificationDto
            {
                Id = notif.Id,
                Type = notif.Type,
                Title = notif.Title,
                Message = notif.Message,
                TargetUrl = notif.TargetUrl ?? string.Empty,
                IsRead = notif.IsRead,
                Category = notif.Category,
                ReferenceId = notif.ReferenceId,
                ActionUser = notif.ActionUser,
                TimeAgo = FormatThaiTimeAgo(notif.CreatedAt, now),
                CreatedAt = notif.CreatedAt
            };

            await _hubContext.Clients.All.SendAsync("ReceiveNotification", new
            {
                item = dto,
                unreadCount = unreadCount
            });

            return notif;
        }

        public async Task<bool> MarkAsReadAsync(int id)
        {
            var notif = await _db.Notifications.FindAsync(id);
            if (notif == null) return false;

            if (!notif.IsRead)
            {
                notif.IsRead = true;
                notif.ReadAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                var staffUnread = await GetUnreadCountAsync(isStaff: true);
                var adminUnread = await GetUnreadCountAsync(isStaff: false);

                await _hubContext.Clients.All.SendAsync("ReceiveNotificationRead", new
                {
                    id = id,
                    staffUnreadCount = staffUnread,
                    adminUnreadCount = adminUnread
                });
            }

            return true;
        }

        public async Task<bool> MarkAllAsReadAsync()
        {
            var unreadItems = await _db.Notifications.Where(n => !n.IsRead).ToListAsync();
            if (unreadItems.Any())
            {
                var now = DateTime.UtcNow;
                foreach (var item in unreadItems)
                {
                    item.IsRead = true;
                    item.ReadAt = now;
                }
                await _db.SaveChangesAsync();

                await _hubContext.Clients.All.SendAsync("ReceiveNotificationAllRead", new
                {
                    unreadCount = 0
                });
            }
            return true;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var notif = await _db.Notifications.FindAsync(id);
            if (notif == null) return false;

            _db.Notifications.Remove(notif);
            await _db.SaveChangesAsync();

            var staffUnread = await GetUnreadCountAsync(isStaff: true);
            var adminUnread = await GetUnreadCountAsync(isStaff: false);

            await _hubContext.Clients.All.SendAsync("ReceiveNotificationDeleted", new
            {
                id = id,
                staffUnreadCount = staffUnread,
                adminUnreadCount = adminUnread
            });

            return true;
        }

        private static string FormatThaiTimeAgo(DateTime createdAt, DateTime now)
        {
            var span = now - createdAt;
            if (span.TotalSeconds < 0) return "เมื่อสักครู่";
            if (span.TotalMinutes < 1) return "เมื่อสักครู่";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} นาทีที่แล้ว";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} ชั่วโมงที่แล้ว";
            if (span.TotalDays < 2) return "เมื่อวานนี้";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} วันที่แล้ว";
            return createdAt.ToString("dd/MM/yyyy");
        }
    }
}
