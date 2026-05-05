using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnitySearch.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Citus.Modules.UnityAi.Application;

/// <summary>
/// Decorates an existing <see cref="IUnitySearchEngine"/> by reranking each
/// returned group through the unityAI <see cref="IUnitysearchRankingEngine"/>.
/// Behaviour:
///   * If the learning flag is off, fall through to the inner engine result
///     unchanged. The legacy ranking remains canonical.
///   * If the inner engine throws, the exception propagates (this decorator
///     never swallows search-layer errors).
///   * If the ranking engine throws, the inner result is returned unchanged
///     and a warning is logged. Reranking must never break search.
///   * Groups with 0 or 1 item are passed through unchanged.
///   * The ranking engine is called per-group so the entity-type filter
///     stays clean (groups are entity-typed in the existing search engine).
///
/// All non-search methods (recent queries, recent selections, click recording)
/// are delegated to the inner engine without modification.
/// </summary>
public sealed class UnitysearchAiRerankingEngine : IUnitySearchEngine
{
    private readonly IUnitySearchEngine _inner;
    private readonly IUnitysearchRankingEngine _ranking;
    private readonly UnityAiFeatureFlagAccessor _flags;
    private readonly ILogger<UnitysearchAiRerankingEngine> _logger;

    public UnitysearchAiRerankingEngine(
        IUnitySearchEngine inner,
        IUnitysearchRankingEngine ranking,
        UnityAiFeatureFlagAccessor flags,
        ILogger<UnitysearchAiRerankingEngine> logger)
    {
        _inner = inner;
        _ranking = ranking;
        _flags = flags;
        _logger = logger;
    }

    public async Task<UnitySearchResult> SearchAsync(UnitySearchQuery query, CancellationToken cancellationToken)
    {
        var result = await _inner.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        if (!_flags.UnitysearchLearningEnabled)
        {
            return result;
        }

        if (result.Groups.Count == 0)
        {
            return result;
        }

        var rerankedGroups = new List<UnitySearchGroupResult>(result.Groups.Count);
        var normalizedQuery = string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim().ToLowerInvariant();

        foreach (var group in result.Groups)
        {
            if (group.Items.Count <= 1)
            {
                rerankedGroups.Add(group);
                continue;
            }

            var rerankedItems = await TryRerankGroupAsync(query, normalizedQuery, group, cancellationToken).ConfigureAwait(false);
            rerankedGroups.Add(rerankedItems is null ? group : group with { Items = rerankedItems });
        }

        return result with { Groups = rerankedGroups };
    }

    public Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        CompanyId companyId, UserId userId, string context, int take, CancellationToken cancellationToken)
        => _inner.ListRecentQueriesAsync(companyId, userId, context, take, cancellationToken);

    public Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(
        CompanyId companyId, UserId userId, string context, int take, CancellationToken cancellationToken)
        => _inner.ListRecentSelectionsAsync(companyId, userId, context, take, cancellationToken);

    public Task RecordClickAsync(
        CompanyId companyId, UserId userId, string context, string entityType, Guid sourceId, CancellationToken cancellationToken)
        => _inner.RecordClickAsync(companyId, userId, context, entityType, sourceId, cancellationToken);

    private async Task<IReadOnlyList<UnitySearchSuggestion>?> TryRerankGroupAsync(
        UnitySearchQuery query,
        string? normalizedQuery,
        UnitySearchGroupResult group,
        CancellationToken cancellationToken)
    {
        try
        {
            // Within a group, EntityType is consistent for the candidates the
            // ranking engine cares about. Use the first item's type; if the
            // group is mixed (rare) we still rerank since the engine treats
            // each entity_id independently.
            var entityType = group.Items[0].EntityType;

            var candidates = group.Items
                .Select(item => new UnitysearchRankingCandidate(
                    EntityId: item.SourceId,
                    EntityType: item.EntityType,
                    DisplayCode: item.PrimaryText,
                    DisplayName: item.SecondaryText,
                    AliasTerms: null,
                    IsActive: true,
                    StatusLabel: null))
                .ToArray();

            var rankingRequest = new UnitysearchRankingRequest(
                CompanyId: query.CompanyId,
                UserId: query.UserId,
                Context: string.IsNullOrWhiteSpace(query.Context) ? "global" : query.Context,
                EntityType: entityType,
                Query: query.SearchText,
                NormalizedQuery: normalizedQuery,
                Anchor: null,
                Candidates: candidates,
                TraceEnabled: false);

            var ranking = await _ranking.RankAsync(rankingRequest, cancellationToken).ConfigureAwait(false);

            // Reorder using the ranked candidate order. If we got back fewer
            // items (defensive), keep the original group; we don't want to
            // silently drop suggestions.
            if (ranking.Ranked.Count != group.Items.Count)
            {
                _logger.LogWarning(
                    "ranking engine returned {Returned} items for {Original} candidates in context {Context}/{EntityType}; keeping original order",
                    ranking.Ranked.Count, group.Items.Count, query.Context, entityType);
                return null;
            }

            var lookup = group.Items.ToDictionary(item => item.SourceId);
            var reordered = new List<UnitySearchSuggestion>(ranking.Ranked.Count);
            foreach (var rankedCandidate in ranking.Ranked)
            {
                if (!lookup.TryGetValue(rankedCandidate.Candidate.EntityId, out var item))
                {
                    _logger.LogWarning(
                        "ranking engine returned unknown entity {EntityId} for context {Context}; keeping original order",
                        rankedCandidate.Candidate.EntityId, query.Context);
                    return null;
                }

                // Surface the ranking score on the suggestion so frontend
                // tooling can show it. The legacy Score is overwritten with
                // the unityAI final score — they have the same purpose.
                reordered.Add(item with { Score = rankedCandidate.Score.FinalScore });
            }

            return reordered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "unityAI reranking failed for context {Context}; falling back to inner engine order",
                query.Context);
            return null;
        }
    }
}
