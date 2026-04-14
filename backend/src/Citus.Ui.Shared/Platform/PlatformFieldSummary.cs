namespace Citus.Ui.Shared.Platform;

public sealed record class PlatformFieldSummary
{
    public string Name { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string FieldType { get; init; } = string.Empty;

    public string SourceColumn { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Required { get; init; }

    public bool Searchable { get; init; }

    public bool Auditable { get; init; }

    public bool System { get; init; }

    public int? MaxLength { get; init; }
}
