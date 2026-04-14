using Citus.Platform.Core.Bootstrap;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformBootstrapper
{
    Task<PlatformBootstrapReport> BootstrapAsync(CancellationToken cancellationToken);
}
