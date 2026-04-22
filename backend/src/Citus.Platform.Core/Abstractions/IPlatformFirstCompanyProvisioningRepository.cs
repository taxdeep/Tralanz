using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformFirstCompanyProvisioningRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<PlatformFirstCompanyProvisioningResult> ProvisionAsync(
        PlatformFirstCompanyProvisioningCommand command,
        CancellationToken cancellationToken);
}
