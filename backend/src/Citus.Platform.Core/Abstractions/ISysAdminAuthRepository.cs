using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

public interface ISysAdminAuthRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<SysAdminSetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken);

    Task EnsureBootstrapAccountAsync(
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken);

    Task<SysAdminFirstAccountProvisioningResult> ProvisionFirstAccountAsync(
        string email,
        string password,
        string displayName,
        CancellationToken cancellationToken);

    Task<SysAdminAuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        TimeSpan sessionLifetime,
        string? remoteIp,
        string? userAgent,
        CancellationToken cancellationToken);

    Task<SysAdminSessionValidationResult> ValidateSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken);

    Task RevokeSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken);

    Task<SysAdminSecretRotationResult> RotateSecretAsync(
        UserId sysAdminAccountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);
}
