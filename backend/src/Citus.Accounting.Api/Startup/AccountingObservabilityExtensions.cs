namespace Citus.Accounting.Api.Startup;

/// <summary>
/// Observability wiring extracted from Program.cs (P2): optional Sentry
/// exception monitoring + tracing. Behavior-preserving.
/// </summary>
public static class AccountingObservabilityExtensions
{
    public static WebApplicationBuilder UseAccountingSentry(this WebApplicationBuilder builder)
    {
        // Optional exception monitoring. Active only when Sentry:Dsn is set —
        // SentryClient.Init no-ops on empty DSN, so the SDK costs nothing when
        // the operator hasn't enrolled a Sentry project. Release tag tracks
        // the auto-bumped Citus version (so an alert on v 0.00.000.0000.05.4M
        // stays linked to that exact build); the environment tag follows
        // ASPNETCORE_ENVIRONMENT (Development / Staging / Production).
        //
        // See deploy/SENTRY.md for how to set Sentry__Dsn on the host.
        builder.WebHost.UseSentry(options =>
        {
            options.Dsn = builder.Configuration["Sentry:Dsn"]
                ?? Environment.GetEnvironmentVariable("SENTRY_DSN")
                ?? string.Empty;
            options.Release = builder.Configuration["CITUS_APP_VERSION"]
                ?? Environment.GetEnvironmentVariable("CITUS_APP_VERSION");
            options.Environment = builder.Environment.EnvironmentName;
            options.AttachStacktrace = true;
            // Don't sample request bodies / cookies / IP — accounting payloads
            // contain customer + amount data. Operators who want full PII can
            // flip this in a custom override.
            options.SendDefaultPii = false;
            // 100% tracing on a single-tenant pilot is fine. Bump to a sample
            // rate once traffic ramps up.
            options.TracesSampleRate = 1.0;
        });
        return builder;
    }
}
