using Citus.Accounting.Api;
using Citus.Ui.Shared.Business;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Citus.Accounting.Api.Startup;

/// <summary>
/// HTTP rate-limiting policies extracted from Program.cs (P2): the per-IP
/// auth-login limiter and the per-(company,user) invoice-send limiter.
/// Identical policy names, windows, and limits.
/// </summary>
public static class AccountingRateLimitingExtensions
{
    public static IServiceCollection AddAccountingRateLimiting(this IServiceCollection services)
    {
        // HTTP-layer rate limiting on /auth/login. The application already has
        // account-level lockout (PostgresPlatformLoginLockoutPolicy) which kicks
        // in after N consecutive failures for the SAME username — but that's
        // orthogonal to the network-speed brute-force threat where an attacker
        // rotates usernames against a single IP. This middleware caps any
        // single IP at 5 login attempts per 60 seconds, then returns 429 for
        // the rest of the window. The same partition fires when an account is
        // already locked (so the lockout response itself can't be probed at
        // network speed for timing leaks). Other /auth/* routes are not
        // rate-limited here — /auth/session and /auth/logout aren't useful
        // brute-force vectors.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth-login", httpContext =>
            {
                var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromSeconds(60),
                        PermitLimit = 5,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
            // P0-5c (C10): rate-limit invoice email send per (user, company) to
            // prevent an authenticated user from blasting customer mailboxes (or
            // exhausting the platform's SMTP reputation budget) via the
            // /document-review/invoice/{id}/send endpoint. The window is generous
            // for legitimate operator use (one invoice every two minutes is well
            // above the realistic billing pace) and cuts off abuse fast.
            options.AddPolicy("invoice-send", httpContext =>
            {
                var userId = (string?)httpContext.Request.Headers[BusinessSessionHeaders.UserId]
                    ?? (string?)httpContext.Request.Headers[BusinessSessionHeaderNames.LegacyUserId];
                var companyId = (string?)httpContext.Request.Headers[BusinessSessionHeaders.ActiveCompanyId]
                    ?? (string?)httpContext.Request.Headers[BusinessSessionHeaderNames.LegacyActiveCompanyId];
                var partitionKey = string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(companyId)
                    ? (httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous")
                    : $"{companyId}|{userId}";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromHours(1),
                        PermitLimit = 30,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });
        return services;
    }
}
