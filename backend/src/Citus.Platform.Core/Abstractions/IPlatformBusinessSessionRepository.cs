using Citus.Platform.Core.Accounts;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformBusinessSessionRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<PlatformBusinessSessionResult> AuthenticateAsync(
        string login,
        string password,
        TimeSpan sessionLifetime,
        string? remoteIp,
        string? userAgent,
        CancellationToken cancellationToken);

    Task<PlatformBusinessSessionResult> ValidateSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken);

    Task<PlatformBusinessSessionResult> SwitchActiveCompanyAsync(
        string sessionToken,
        Guid activeCompanyId,
        CancellationToken cancellationToken);

    Task RevokeSessionAsync(
        string sessionToken,
        CancellationToken cancellationToken);
}
