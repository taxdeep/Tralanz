using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Bootstrap;
using Citus.Platform.Core.BuiltIn;
using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;

namespace Citus.Platform.Core.Services;

public sealed class PlatformCoreBootstrapper(
    IPlatformMetadataRepository repository,
    IPlatformMetadataService metadataService) : IPlatformBootstrapper
{
    public async Task<PlatformBootstrapReport> BootstrapAsync(CancellationToken cancellationToken)
    {
        var entities = CitusPlatformKernel.GetBuiltInEntities();
        var modules = NormalizeModules(CitusPlatformKernel.GetBuiltInModules(), entities);

        foreach (var module in modules)
        {
            await repository.UpsertModuleAsync(module, cancellationToken);
        }

        foreach (var entity in entities)
        {
            await metadataService.UpsertEntityAsync(entity, cancellationToken);
        }

        return new PlatformBootstrapReport
        {
            ModulesSeeded = modules.Count,
            EntitiesSeeded = entities.Count,
            ModuleKeys = modules.Select(module => module.Key).ToArray(),
            EntityNames = entities.Select(entity => entity.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray()
        };
    }

    private static IReadOnlyList<PlatformModuleManifest> NormalizeModules(
        IReadOnlyList<PlatformModuleManifest> modules,
        IReadOnlyList<CoreEntityDefinition> entities) =>
        modules
            .Select(module => module with
            {
                Key = module.Key.Trim().ToLowerInvariant(),
                Name = module.Name.Trim(),
                Description = module.Description?.Trim() ?? string.Empty,
                RoutePrefix = string.IsNullOrWhiteSpace(module.RoutePrefix) ? "/" : module.RoutePrefix.Trim(),
                Capabilities = module.Capabilities
                    .Where(capability => !string.IsNullOrWhiteSpace(capability))
                    .Select(capability => capability.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(capability => capability, StringComparer.Ordinal)
                    .ToArray(),
                EntityNames = entities
                    .Where(entity => entity.ModuleKey == module.Key.Trim().ToLowerInvariant())
                    .Select(entity => entity.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray()
            })
            .ToArray();
}
