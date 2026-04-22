using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Services;
using Citus.Ui.Shared.Control;

namespace Web.Shell;

public static class PlatformNotificationApi
{
    public static IEndpointRouteBuilder MapPlatformNotificationApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/platform/notification-readiness",
            async (
                HttpContext httpContext,
                IPlatformBusinessSessionRepository businessSessions,
                IPlatformNotificationReadinessWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var authentication = await PlatformBusinessSessionAuthorization.TryResolveUserIdAsync(
                    httpContext,
                    businessSessions,
                    cancellationToken);
                if (!authentication.Succeeded)
                {
                    return authentication.Error!;
                }

                var report = await workflow.GetAsync(cancellationToken);
                return Results.Ok(ToSummary(report));
            });

        return endpoints;
    }

    private static NotificationReadinessSummary ToSummary(
        Citus.Platform.Core.Runtime.PlatformNotificationReadinessReport report) =>
        new()
        {
            ConfigPresent = report.ConfigPresent,
            TestStatus = report.TestStatus,
            LastTestedAtUtc = report.LastTestedAtUtc,
            VerificationReady = report.VerificationReady,
            IsVerificationDeliveryReady = report.IsVerificationDeliveryReady,
            BlockingReason = report.BlockingReason,
            ConfigurationError = report.ConfigurationError
        };
}
