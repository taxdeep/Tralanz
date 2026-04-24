namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchGroupResult
{
    public string GroupKey { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<UnitySearchSuggestion> Items { get; init; } = Array.Empty<UnitySearchSuggestion>();
}
