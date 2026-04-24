namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchRecentQueryRecord
{
    public string Context { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public DateTimeOffset UsedAtUtc { get; init; }
}
