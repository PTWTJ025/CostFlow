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

        public DateTimeProvider(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
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
                        return parsedQueryDate;
                    }

                    // 2. Check Cookie: MockSystemDate
                    if (context.Request.Cookies.TryGetValue("MockSystemDate", out var cookieDate)
                        && DateTime.TryParse(cookieDate, out var parsedCookieDate))
                    {
                        return parsedCookieDate;
                    }
                }

                return DateTime.Now;
            }
        }
    }
}
