using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;

namespace Citus.Platform.Core.Services;

public sealed class PlatformMetadataService(IPlatformMetadataRepository repository) : IPlatformMetadataService
{
    public Task<IReadOnlyList<PlatformModuleManifest>> ListModulesAsync(CancellationToken cancellationToken) =>
        repository.ListModulesAsync(cancellationToken);

    public Task<IReadOnlyList<CoreEntityDefinition>> ListEntitiesAsync(CancellationToken cancellationToken) =>
        repository.ListEntitiesAsync(cancellationToken);

    public Task<CoreEntityDefinition?> GetEntityAsync(string entityName, CancellationToken cancellationToken) =>
        repository.GetEntityAsync(NormalizeToken(entityName, nameof(entityName)), cancellationToken);

    public Task UpsertEntityAsync(CoreEntityDefinition entityDefinition, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEntity(entityDefinition);
        return repository.UpsertEntityAsync(normalized, cancellationToken);
    }

    public static CoreEntityDefinition NormalizeEntity(CoreEntityDefinition entityDefinition)
    {
        ArgumentNullException.ThrowIfNull(entityDefinition);

        var normalizedFields = entityDefinition.Fields
            .Select(NormalizeField)
            .ToArray();

        var duplicateField = normalizedFields
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateField is not null)
        {
            throw new InvalidOperationException($"Entity '{entityDefinition.Name}' contains a duplicate field named '{duplicateField.Key}'.");
        }

        return entityDefinition with
        {
            ModuleKey = NormalizeToken(entityDefinition.ModuleKey, nameof(entityDefinition.ModuleKey)),
            Name = NormalizeToken(entityDefinition.Name, nameof(entityDefinition.Name)),
            Label = NormalizeLabel(entityDefinition.Label, nameof(entityDefinition.Label)),
            LabelPlural = string.IsNullOrWhiteSpace(entityDefinition.LabelPlural)
                ? NormalizeLabel(entityDefinition.Label, nameof(entityDefinition.LabelPlural)) + "s"
                : NormalizeLabel(entityDefinition.LabelPlural, nameof(entityDefinition.LabelPlural)),
            Description = entityDefinition.Description?.Trim() ?? string.Empty,
            StorageTable = NormalizeToken(
                string.IsNullOrWhiteSpace(entityDefinition.StorageTable)
                    ? entityDefinition.Name
                    : entityDefinition.StorageTable,
                nameof(entityDefinition.StorageTable)),
            Fields = normalizedFields,
            Permissions = NormalizePermissions(entityDefinition.Permissions)
        };
    }

    private static CoreFieldDefinition NormalizeField(CoreFieldDefinition fieldDefinition)
    {
        ArgumentNullException.ThrowIfNull(fieldDefinition);

        return fieldDefinition with
        {
            Name = NormalizeToken(fieldDefinition.Name, nameof(fieldDefinition.Name)),
            Label = NormalizeLabel(fieldDefinition.Label, nameof(fieldDefinition.Label)),
            FieldType = NormalizeToken(fieldDefinition.FieldType, nameof(fieldDefinition.FieldType)),
            SourceColumn = NormalizeToken(
                string.IsNullOrWhiteSpace(fieldDefinition.SourceColumn)
                    ? fieldDefinition.Name
                    : fieldDefinition.SourceColumn,
                nameof(fieldDefinition.SourceColumn)),
            Description = fieldDefinition.Description?.Trim() ?? string.Empty
        };
    }

    private static CoreEntityPermissionSet NormalizePermissions(CoreEntityPermissionSet? permissions) =>
        new()
        {
            Create = NormalizePermissionList(permissions?.Create),
            Read = NormalizePermissionList(permissions?.Read),
            Update = NormalizePermissionList(permissions?.Update),
            Delete = NormalizePermissionList(permissions?.Delete)
        };

    private static IReadOnlyList<string> NormalizePermissionList(IReadOnlyList<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray() ??
        Array.Empty<string>();

    private static string NormalizeToken(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{parameterName} is required.");
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLabel(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{parameterName} is required.");
        }

        return value.Trim();
    }
}
