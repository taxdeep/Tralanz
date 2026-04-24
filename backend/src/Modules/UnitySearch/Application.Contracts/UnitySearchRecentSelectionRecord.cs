namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchRecentSelectionRecord
{
    public required Guid SourceId { get; init; }

    public string EntityType { get; init; } = string.Empty;

    public string GroupKey { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public string NavigationHref { get; init; } = string.Empty;

    public int ClickCount { get; init; }

    public DateTimeOffset LastClickedAtUtc { get; init; }
}
