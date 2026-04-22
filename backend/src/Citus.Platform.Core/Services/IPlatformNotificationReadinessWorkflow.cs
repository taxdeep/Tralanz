using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Services;

public interface IPlatformNotificationReadinessWorkflow
{
    Task<PlatformNotificationReadinessReport> GetAsync(CancellationToken cancellationToken);

    Task<PlatformNotificationTestSendResult> SendTestAsync(
        string destination,
        string recipientDisplayName,
        CancellationToken cancellationToken);
}
