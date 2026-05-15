using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Bootstrap;
using Citus.Platform.Core.BuiltIn;
using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;
using Citus.Platform.Core.Services;
using Xunit;

namespace Citus.Platform.Core.Tests;

public sealed class PlatformCoreBootstrapperTests
{
    [Fact]
    public async Task BootstrapSeedsBuiltInModulesAndEntities()
    {
        var repository = new InMemoryPlatformMetadataRepository();
        var metadataService = new PlatformMetadataService(repository);
        var bootstrapper = new PlatformCoreBootstrapper(repository, metadataService);

        PlatformBootstrapReport report = await bootstrapper.BootstrapAsync(CancellationToken.None);

        Assert.Equal(CitusPlatformKernel.GetBuiltInModules().Count, report.ModulesSeeded);
        Assert.Equal(CitusPlatformKernel.GetBuiltInEntities().Count, report.EntitiesSeeded);
        Assert.Contains(PlatformModuleKeys.Accounting, report.ModuleKeys);
        Assert.Contains("journal_entries", report.EntityNames);
        Assert.False(repository.SchemaEnsured);
    }

    [Fact]
    public async Task MetadataServiceNormalizesEntityShapeBeforePersisting()
    {
        var repository = new InMemoryPlatformMetadataRepository();
        var service = new PlatformMetadataService(repository);

        await service.UpsertEntityAsync(
            new CoreEntityDefinition
            {
                Id = Guid.NewGuid(),
                ModuleKey = " Accounting ",
                Name = "  Custom_Entity  ",
                Label = "Custom Entity",
                LabelPlural = "",
                Description = "  Example  ",
                StorageTable = "  Custom_Table  ",
                CompanyScoped = true,
                SystemScoped = false,
                Fields =
                [
                    new CoreFieldDefinition
                    {
                        Name = "  Display_Name  ",
                        Label = "Display Name",
                        FieldType = " Text ",
                        SourceColumn = "  Display_Name  ",
                        Required = true,
                        Searchable = true
                    }
                ],
                Permissions = new CoreEntityPermissionSet
                {
                    Read = [" Owner ", "owner", "Auditor"]
                }
            },
            CancellationToken.None);

        CoreEntityDefinition? entity = await service.GetEntityAsync("custom_entity", CancellationToken.None);

        Assert.NotNull(entity);
        Assert.Equal("accounting", entity!.ModuleKey);
        Assert.Equal("custom_entity", entity.Name);
        Assert.Equal("Custom Entitys", entity.LabelPlural);
        Assert.Equal("custom_table", entity.StorageTable);
        Assert.Equal("display_name", entity.Fields.Single().Name);
        Assert.Equal("text", entity.Fields.Single().FieldType);
        Assert.Equal(["auditor", "owner"], entity.Permissions.Read);
    }

    private sealed class InMemoryPlatformMetadataRepository : IPlatformMetadataRepository
    {
        private readonly Dictionary<string, PlatformModuleManifest> modules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CoreEntityDefinition> entities = new(StringComparer.Ordinal);

        public bool SchemaEnsured { get; private set; }

        public Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            SchemaEnsured = true;
            return Task.CompletedTask;
        }

        public Task UpsertModuleAsync(PlatformModuleManifest moduleManifest, CancellationToken cancellationToken)
        {
            modules[moduleManifest.Key] = moduleManifest;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlatformModuleManifest>> ListModulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlatformModuleManifest>>(modules.Values.OrderBy(module => module.Key, StringComparer.Ordinal).ToArray());

        public Task UpsertEntityAsync(CoreEntityDefinition entityDefinition, CancellationToken cancellationToken)
        {
            entities[entityDefinition.Name] = entityDefinition;
            return Task.CompletedTask;
        }

        public Task<CoreEntityDefinition?> GetEntityAsync(string entityName, CancellationToken cancellationToken)
        {
            entities.TryGetValue(entityName, out var entity);
            return Task.FromResult(entity);
        }

        public Task<IReadOnlyList<CoreEntityDefinition>> ListEntitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CoreEntityDefinition>>(entities.Values.OrderBy(entity => entity.Name, StringComparer.Ordinal).ToArray());
    }
}
