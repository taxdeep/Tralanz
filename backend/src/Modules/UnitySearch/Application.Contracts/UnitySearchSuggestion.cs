namespace Citus.Modules.UnitySearch.Application.Contracts;

public sealed record class UnitySearchSuggestion
{
    public required Guid SourceId { get; init; }

    public string EntityType { get; init; } = string.Empty;

    public string GroupKey { get; init; } = string.Empty;

    public string PrimaryText { get; init; } = string.Empty;

    public string SecondaryText { get; init; } = string.Empty;

    public string NavigationHref { get; init; } = string.Empty;

    public string MetadataJson { get; init; } = "{}";

    public DateOnly? EffectiveDate { get; init; }

    public decimal? Amount { get; init; }

    public decimal Score { get; init; }
}
