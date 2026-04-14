using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformMetadataService
{
    Task<IReadOnlyList<PlatformModuleManifest>> ListModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CoreEntityDefinition>> ListEntitiesAsync(CancellationToken cancellationToken);

    Task<CoreEntityDefinition?> GetEntityAsync(string entityName, CancellationToken cancellationToken);

    Task UpsertEntityAsync(CoreEntityDefinition entityDefinition, CancellationToken cancellationToken);
}
