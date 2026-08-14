using Microsoft.AspNetCore.Http;
using System;

namespace CostFlow.Services
{
    public interface IDateTimeProvider
    {
        DateTime Now { get; }
    }

    public class DateTimeProvider : IDateTimeProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMockDateStore _mockDateStore;

        public DateTimeProvider(IHttpContextAccessor httpContextAccessor, IMockDateStore mockDateStore)
        {
            _httpContextAccessor = httpContextAccessor;
            _mockDateStore = mockDateStore;
        }

        public DateTime Now
        {
            get
            {
                var context = _httpContextAccessor.HttpContext;
                if (context != null)
                {
                    // 1. Check Query Parameter: ?mockDate=YYYY-MM-DD
                    if (context.Request.Query.TryGetValue("mockDate", out var qDate)
                        && DateTime.TryParse(qDate, out var parsedQueryDate))
                    {
                        context.Response.Cookies.Append("MockSystemDate", parsedQueryDate.ToString("yyyy-MM-dd"), new CookieOptions
                        {
                            Expires = DateTimeOffset.Now.AddDays(7),
                            HttpOnly = true,
                            IsEssential = true
                        });
                        _mockDateStore.MockDate = parsedQueryDate;
                        return parsedQueryDate;
                    }

                    // 2. Check Cookie: MockSystemDate
                    if (context.Request.Cookies.TryGetValue("MockSystemDate", out var cookieDate)
                        && DateTime.TryParse(cookieDate, out var parsedCookieDate))
                    {
                        _mockDateStore.MockDate = parsedCookieDate;
                        return parsedCookieDate;
                    }
                }

                // 3. Fallback to Singleton MockDateStore (สำหรับ Background Service หรือเมื่อไม่มีใน Context)
                if (_mockDateStore.MockDate.HasValue)
                {
                    return _mockDateStore.MockDate.Value;
                }

                // 4. Fallback สุดท้าย: เวลาจริงของระบบ
                return DateTime.Now;
            }
        }
    }
}

