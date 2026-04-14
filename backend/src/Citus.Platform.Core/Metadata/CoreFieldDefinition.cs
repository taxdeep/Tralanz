namespace Citus.Platform.Core.Metadata;

public sealed record class CoreFieldDefinition
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
