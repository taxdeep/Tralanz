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
///
/// <see cref="Intent"/> is the optional Plan B addition. Populated from
/// the per-company unitysearch_query_intent_cache when the engine finds
/// a ready row for (company_id, query_hash). Null when no cache hit —
/// the SQL ranker still runs full Plan A behavior, no degradation.
/// </summary>
public sealed record class UnitySearchQueryHints(string QueryClassTag, decimal? NumericValue)
{
    public static readonly UnitySearchQueryHints None = new("empty", null);

    public bool IsNumeric => NumericValue.HasValue;

    public UnitysearchQueryIntent? Intent { get; init; }

    /// <summary>
    /// Plan C: pgvector query embedding, serialised as the pgvector
    /// text literal (e.g. <c>[0.012,0.034,...]</c>). The SQL ranker
    /// casts it to <c>vector</c> and uses it for the L5 semantic
    /// candidate + scoring tier. Null until the embedding provider +
    /// query-embedding cache land — that's the natural no-op state
    /// for the Plan C foundation batch.
    /// </summary>
    public string? QueryEmbeddingLiteral { get; init; }
}

/// <summary>
/// Plan B cache value. Lives in <c>unitysearch_query_intent_cache</c> per
/// (company_id, query_hash). All fields are best-effort suggestions —
/// the deterministic Plan A scoring still runs in full on top, and the
/// intent boosts are capped strictly below the exact/prefix tiers so a
/// clean match always wins.
/// </summary>
public sealed record class UnitysearchQueryIntent(
    /// <summary>
    /// Per-entity-type weight in [0, 1]. Multiplied by 25 (cap) and
    /// added to the doc's score when doc.entity_type matches one of
    /// the keys. Empty dictionary = no per-entity bias.
    /// </summary>
    IReadOnlyDictionary<string, decimal> EntityTypePriors,
    /// <summary>
    /// Operator-coined or LLM-distilled synonyms. Each term is OR-ed
    /// into the FTS candidate gate via
    /// <c>doc.search_vector @@ websearch_to_tsquery('simple', term)</c>.
    /// Empty = no recall expansion.
    /// </summary>
    IReadOnlyList<string> ExpandedTerms,
    /// <summary>
    /// Overall confidence reported by the source (LLM / operator).
    /// Currently informational; future iterations may use it to scale
    /// boosts.
    /// </summary>
    decimal Confidence)
{
    /// <summary>
    /// Plan C: pgvector text literal (e.g. <c>[0.012, 0.034, ...]</c>)
    /// of the normalized query's embedding. Threaded through to the
    /// SQL ranker via <see cref="UnitySearchQueryHints.QueryEmbeddingLiteral"/>
    /// to drive vector candidate gate + scoring. Null when the cache
    /// row was filled before embeddings were enabled, or when the
    /// embedding provider returned a non-Succeeded outcome — the
    /// intent (priors + terms) is still usable on its own.
    /// </summary>
    public string? QueryEmbeddingLiteral { get; init; }
}
