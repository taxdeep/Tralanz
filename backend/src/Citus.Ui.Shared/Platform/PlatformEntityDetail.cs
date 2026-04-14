namespace Citus.Ui.Shared.Platform;

public sealed record class PlatformEntityDetail
{
    public Guid Id { get; init; }

    public string ModuleKey { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string LabelPlural { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string StorageTable { get; init; } = string.Empty;

    public bool CompanyScoped { get; init; }

    public bool SystemScoped { get; init; }

    public IReadOnlyList<PlatformFieldSummary> Fields { get; init; } = Array.Empty<PlatformFieldSummary>();

    public PlatformPermissionSummary Permissions { get; init; } = new();
}
