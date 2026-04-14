using Citus.Platform.Core.Metadata;

namespace Citus.SysAdmin.Api;

public sealed record class UpsertCoreEntityHttpRequest
{
    public Guid? Id { get; init; }

    public required string ModuleKey { get; init; }

    public required string Name { get; init; }

    public required string Label { get; init; }

    public string LabelPlural { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string StorageTable { get; init; } = string.Empty;

    public bool CompanyScoped { get; init; }

    public bool SystemScoped { get; init; }

    public IReadOnlyList<UpsertCoreFieldHttpRequest> Fields { get; init; } = Array.Empty<UpsertCoreFieldHttpRequest>();

    public CoreEntityPermissionSet Permissions { get; init; } = new();
}

public sealed record class UpsertCoreFieldHttpRequest
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required string FieldType { get; init; }

    public string SourceColumn { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Required { get; init; }

    public bool Searchable { get; init; }

    public bool Auditable { get; init; }

    public bool System { get; init; }

    public int? MaxLength { get; init; }
}

public static class PlatformCoreContractMapper
{
    public static CoreEntityDefinition ToEntityDefinition(this UpsertCoreEntityHttpRequest request) =>
        new()
        {
            Id = request.Id ?? Guid.NewGuid(),
            ModuleKey = request.ModuleKey,
            Name = request.Name,
            Label = request.Label,
            LabelPlural = request.LabelPlural,
            Description = request.Description,
            StorageTable = request.StorageTable,
            CompanyScoped = request.CompanyScoped,
            SystemScoped = request.SystemScoped,
            Fields = request.Fields
                .Select(field => new CoreFieldDefinition
                {
                    Name = field.Name,
                    Label = field.Label,
                    FieldType = field.FieldType,
                    SourceColumn = field.SourceColumn,
                    Description = field.Description,
                    Required = field.Required,
                    Searchable = field.Searchable,
                    Auditable = field.Auditable,
                    System = field.System,
                    MaxLength = field.MaxLength
                })
                .ToArray(),
            Permissions = request.Permissions
        };
}
