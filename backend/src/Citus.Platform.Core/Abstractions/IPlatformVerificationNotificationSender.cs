using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformVerificationNotificationSender
{
    string ProviderKey { get; }

    string? GetConfigurationError();

    Task<PlatformNotificationSendResult> SendVerificationAsync(
        PlatformVerificationNotificationMessage message,
        CancellationToken cancellationToken);

    Task<PlatformNotificationSendResult> SendPasswordResetAsync(
        PasswordResetNotificationMessage message,
        CancellationToken cancellationToken);
}
