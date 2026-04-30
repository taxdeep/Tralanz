using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchQueryService
{
    Task<IReadOnlyList<SearchDocumentRecord>> SearchDocumentsAsync(
        UnitySearchQuery query,
        SearchPolicyDefinition policy,
        string normalizedQuery,
        UnitySearchQueryHints hints,
        CancellationToken cancellationToken);
}

/// <summary>
/// Pre-classified shape of the query the engine hands to the SQL service.
/// Carries the query-class tag (text / numeric_decimal / etc) and the
/// parsed numeric value when applicable, so the SQL ranker can light up
/// the amount-tier path and the per-user query-class prior join without
/// re-parsing.
/// </summary>
public sealed record class UnitySearchQueryHints(string QueryClassTag, decimal? NumericValue)
{
    public static readonly UnitySearchQueryHints None = new("empty", null);

    public bool IsNumeric => NumericValue.HasValue;
}
