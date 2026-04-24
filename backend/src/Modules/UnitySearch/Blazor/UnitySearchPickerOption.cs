namespace Citus.Modules.UnitySearch.Blazor;

public sealed record class UnitySearchPickerOption
{
    public required Guid SourceId { get; init; }

    public required string EntityType { get; init; }

    public required string PrimaryText { get; init; }

    public string SecondaryText { get; init; } = string.Empty;

    public string DisplayText { get; init; } = string.Empty;

    public string NavigationHref { get; init; } = string.Empty;
}
