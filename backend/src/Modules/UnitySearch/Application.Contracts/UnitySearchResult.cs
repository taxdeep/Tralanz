namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchResult
{
    public string QueryText { get; init; } = string.Empty;

    public string Context { get; init; } = string.Empty;

    public IReadOnlyList<UnitySearchGroupResult> Groups { get; init; } = Array.Empty<UnitySearchGroupResult>();

    public IReadOnlyList<UnitySearchRecentQueryRecord> RecentQueries { get; init; } = Array.Empty<UnitySearchRecentQueryRecord>();

    public IReadOnlyList<UnitySearchRecentSelectionRecord> RecentSelections { get; init; } = Array.Empty<UnitySearchRecentSelectionRecord>();

    public int TotalCount { get; init; }
}
