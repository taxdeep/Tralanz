namespace Citus.Modules.UnitySearch.Blazor;

public sealed record class UnitySearchPickerOption
{
    public required Guid SourceId { get; init; }

    public required string EntityType { get; init; }

    public required string PrimaryText { get; init; }

    public string SecondaryText { get; init; } = string.Empty;

    public string DisplayText { get; init; } = string.Empty;

    public string NavigationHref { get; init; } = string.Empty;

    /// <summary>
    /// Raw <c>metadata_json</c> from the projection row, surfaced so
    /// callers can recover entity-specific fields that don't fit the
    /// generic option shape — most importantly the char(7) user_id
    /// for <c>user.picker</c> options where <see cref="SourceId"/> is
    /// a synthetic hash. Empty JSON object when the projection
    /// doesn't carry metadata.
    /// </summary>
    public string MetadataJson { get; init; } = "{}";
}
