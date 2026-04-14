using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;

namespace Citus.Platform.Core.Abstractions;

public interface IPlatformMetadataRepository
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task UpsertModuleAsync(PlatformModuleManifest moduleManifest, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlatformModuleManifest>> ListModulesAsync(CancellationToken cancellationToken);

    Task UpsertEntityAsync(CoreEntityDefinition entityDefinition, CancellationToken cancellationToken);

    Task<CoreEntityDefinition?> GetEntityAsync(string entityName, CancellationToken cancellationToken);

    Task<IReadOnlyList<CoreEntityDefinition>> ListEntitiesAsync(CancellationToken cancellationToken);
}
