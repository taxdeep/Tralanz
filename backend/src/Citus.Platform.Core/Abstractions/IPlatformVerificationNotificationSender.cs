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

    /// <summary>
    /// Sends a self-serve password-reset email — the recipient clicks
    /// the link to land on the reset page. Distinct from
    /// <see cref="SendPasswordResetAsync"/>, which carries a 6-digit
    /// code that the operator types into a SysAdmin-driven reset
    /// dialog.
    /// </summary>
    Task<PlatformNotificationSendResult> SendPasswordResetLinkAsync(
        PasswordResetLinkNotificationMessage message,
        CancellationToken cancellationToken);
}
