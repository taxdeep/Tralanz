namespace Citus.Platform.Core.Metadata;

public sealed record class CoreEntityDefinition
{
    public required Guid Id { get; init; }

    public required string ModuleKey { get; init; }

    public required string Name { get; init; }

    public required string Label { get; init; }

    public string LabelPlural { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string StorageTable { get; init; } = string.Empty;

    public bool CompanyScoped { get; init; }

    public bool SystemScoped { get; init; }

    public IReadOnlyList<CoreFieldDefinition> Fields { get; init; } = Array.Empty<CoreFieldDefinition>();

    public CoreEntityPermissionSet Permissions { get; init; } = new();
}
