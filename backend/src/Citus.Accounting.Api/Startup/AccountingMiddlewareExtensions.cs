using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Citus.Accounting.Api.Startup;

/// <summary>
/// Composes the Accounting.Api HTTP middleware pipeline (P5). Preserves the
/// original Sentry tracing + rate limiter and adds minimal, reversible
/// hardening: forwarded-headers support (so per-IP rate limiting works behind
/// a reverse proxy), conservative security response headers, a generic
/// exception handler that never leaks exception text, and a config-driven
/// network guard for the /internal/* operational endpoints.
/// </summary>
public static class AccountingMiddlewareExtensions
{
    public static WebApplication UseAccountingPipeline(this WebApplication app)
    {
        // 1) Forwarded headers FIRST so RemoteIpAddress / scheme reflect the
        //    real client behind a TLS-terminating reverse proxy. Trust is
        //    opt-in via config (ForwardedHeaders:KnownProxies); with none set
        //    the framework default (loopback only) applies, so local dev and
        //    current behavior are unchanged until an operator configures it.
        app.UseForwardedHeaders(BuildForwardedHeadersOptions(app.Configuration));

        // 2) Security response headers on every response (registered via
        //    OnStarting so they survive the exception-handler re-execute).
        ApplySecurityHeaders(app);

        // 3) Generic exception handler: an unhandled exception becomes an
        //    RFC7807-ish 500 with NO exception text (Sentry keeps the detail).
        //    Per-endpoint try/catch contracts are untouched -- they handle and
        //    return before anything reaches here.
        app.UseExceptionHandler(static branch => branch.Run(static async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "about:blank",
                title = "An unexpected error occurred.",
                status = 500
            });
        }));

        // 4) HSTS outside Development (no-op for plain-HTTP local dev).
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        // 5) Original pipeline, unchanged in order.
        app.UseSentryTracing();
        app.UseRateLimiter();

        // 6) Network guard for the /internal/* operational triggers. Log-only
        //    by default (no behavior change); set InternalEndpoints:Enforce=true
        //    to reject calls from outside loopback + InternalEndpoints:AllowedAddresses.
        ApplyInternalEndpointGuard(app);

        return app;
    }

    private static ForwardedHeadersOptions BuildForwardedHeadersOptions(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }

        return options;
    }

    private static void ApplySecurityHeaders(WebApplication app) =>
        app.Use(static (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "SAMEORIGIN";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
                return Task.CompletedTask;
            }, context);
            return next(context);
        });

    private static void ApplyInternalEndpointGuard(WebApplication app)
    {
        var enforce = app.Configuration.GetValue<bool?>("InternalEndpoints:Enforce") ?? false;
        var allowed = (app.Configuration.GetSection("InternalEndpoints:AllowedAddresses").Get<string[]>() ?? [])
            .Select(static value => IPAddress.TryParse(value, out var ip) ? ip : null)
            .Where(static ip => ip is not null)
            .Select(static ip => ip!)
            .ToArray();

        app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/internal"),
            branch => branch.Use(async (context, next) =>
            {
                var remote = context.Connection.RemoteIpAddress;
                var permitted = remote is not null
                    && (IPAddress.IsLoopback(remote) || allowed.Any(ip => ip.Equals(remote)));

                if (!permitted)
                {
                    var logger = context.RequestServices
                        .GetRequiredService<ILogger<InternalEndpointNetworkGuard>>();
                    if (enforce)
                    {
                        logger.LogWarning(
                            "Blocked internal endpoint {Path} from non-allowed address {Remote}.",
                            context.Request.Path, remote);
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    logger.LogWarning(
                        "Internal endpoint {Path} reached from non-allowed address {Remote}; enforcement disabled.",
                        context.Request.Path, remote);
                }

                await next(context);
            }));
    }

    // Marker type only used as the ILogger<T> category for the network guard.
    private sealed class InternalEndpointNetworkGuard;
}
