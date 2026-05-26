using System.Security.Cryptography;
using System.Text;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Modules.UnitySearch.Application;

public sealed class UnitySearchEngine(
    UnitySearchPolicyRegistry policyRegistry,
    IUnitySearchProjectionStore projectionStore,
    IUnitySearchQueryService queryService,
    IUnitySearchStatsStore statsStore,
    IUnitysearchQueryIntentCacheStore intentCacheStore,
    IUnitysearchQueryIntentBackfillEnqueuer intentBackfillEnqueuer) : IUnitySearchEngine
{
    public async Task<UnitySearchResult> SearchAsync(UnitySearchQuery query, CancellationToken cancellationToken)
    {
        var normalizedQuery = UnitySearchCanonicalizer.Normalize(query.SearchText);
        var policy = policyRegistry.Resolve(query.Context);
        await projectionStore.EnsureProjectionFreshAsync(query.CompanyId, cancellationToken);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var recentQueries = query.UserId.HasValue
                ? await statsStore.ListRecentQueriesAsync(
                    query.CompanyId,
                    query.UserId.Value,
                    query.Context,
                    Math.Clamp(query.Take, 1, 10),
                    cancellationToken)
                : Array.Empty<UnitySearchRecentQueryRecord>();
            var recentSelections = query.UserId.HasValue
                ? await ListRecentSelectionsAsync(
                    query.CompanyId,
                    query.UserId.Value,
                    query.Context,
                    Math.Clamp(query.Take, 1, 8),
                    cancellationToken)
                : Array.Empty<UnitySearchRecentSelectionRecord>();

            if (recentSelections.Count > 0)
            {
                return new UnitySearchResult
                {
                    QueryText = string.Empty,
                    Context = query.Context,
                    RecentQueries = recentQueries,
                    RecentSelections = recentSelections
                };
            }

            var defaultDocuments = await queryService.SearchDocumentsAsync(
                query,
                policy,
                normalizedQuery,
                UnitySearchQueryHints.None,
                cancellationToken);

            var defaultGroups = BuildGroups(defaultDocuments);
            return new UnitySearchResult
            {
                QueryText = string.Empty,
                Context = query.Context,
                RecentQueries = recentQueries,
                RecentSelections = recentSelections,
                Groups = defaultGroups,
                TotalCount = defaultDocuments.Count
            };
        }

        var classification = UnitySearchQueryClassifier.Classify(normalizedQuery);
        var hints = new UnitySearchQueryHints(classification.Tag, classification.NumericValue);

        // Plan B: consult the per-company intent cache. A 'ready' hit
        // populates UnitySearchQueryHints.Intent and the SQL ranker
        // applies the per-entity priors and OR-extends the FTS gate
        // with synonyms. A miss is silent — the SQL ranker still runs
        // full Plan A behaviour, and we enqueue a fire-and-forget
        // backfill so the next search for the same (company, query)
        // pair benefits. AI-disabled is indistinguishable from a miss.
        var queryHash = ComputeQueryHash(normalizedQuery);
        UnitysearchQueryIntent? intent = null;
        try
        {
            intent = await intentCacheStore.GetReadyAsync(query.CompanyId, queryHash, cancellationToken);
        }
        catch
        {
            // Cache lookup failure must never break search. Fall through.
        }

        if (intent is not null)
        {
            hints = hints with { Intent = intent };
        }
        else
        {
            // Fire-and-forget — never await. The enqueuer takes its own
            // DI scope, runs the gateway call off-band, writes the cache
            // row. Errors are swallowed inside the enqueuer.
            intentBackfillEnqueuer.Enqueue(
                query.CompanyId,
                normalizedQuery,
                queryHash,
                policy.EntityTypes.ToArray());
        }

        var documents = await queryService.SearchDocumentsAsync(query, policy, normalizedQuery, hints, cancellationToken);
        if (query.UserId.HasValue)
        {
            await statsStore.RecordQueryAsync(query.CompanyId, query.UserId.Value, query.Context, normalizedQuery, cancellationToken);
        }

        var grouped = BuildGroups(documents);

        return new UnitySearchResult
        {
            QueryText = query.SearchText.Trim(),
            Context = query.Context,
            Groups = grouped,
            TotalCount = documents.Count
        };
    }

    private static UnitySearchGroupResult[] BuildGroups(IReadOnlyList<SearchDocumentRecord> documents) =>
        documents
            .GroupBy(static document => document.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UnitySearchGroupResult
            {
                GroupKey = group.Key,
                Title = ResolveGroupTitle(group.Key),
                Items = group
                    .Select(document => new UnitySearchSuggestion
                    {
                        SourceId = document.SourceId,
                        EntityType = document.EntityType,
                        GroupKey = document.GroupKey,
                        PrimaryText = document.PrimaryText,
                        SecondaryText = document.SecondaryText,
                        NavigationHref = document.NavigationHref,
                        MetadataJson = document.MetadataJson,
                        EffectiveDate = document.EffectiveDate,
                        Amount = document.Amount,
                        Score = document.ComputedScore
                    })
                    .ToArray()
            })
            .OrderBy(group => ResolveGroupOrder(group.GroupKey))
            .ToArray();

    public Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        CompanyId companyId,
        UserId userId,
        string context,
        int take,
        CancellationToken cancellationToken) =>
        statsStore.ListRecentQueriesAsync(companyId, userId, context, take, cancellationToken);

    public async Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(
        CompanyId companyId,
        UserId userId,
        string context,
        int take,
        CancellationToken cancellationToken)
    {
        await projectionStore.EnsureProjectionFreshAsync(companyId, cancellationToken);
        return await statsStore.ListRecentSelectionsAsync(companyId, userId, context, take, cancellationToken);
    }

    public Task RecordClickAsync(
        CompanyId companyId,
        UserId userId,
        string context,
        string entityType,
        Guid sourceId,
        CancellationToken cancellationToken) =>
        statsStore.RecordClickAsync(companyId, userId, context, entityType, sourceId, cancellationToken);

    private static string ResolveGroupTitle(string groupKey) =>
        groupKey.Trim().ToLowerInvariant() switch
        {
            SearchGroupKey.JumpTo => "Jump to",
            SearchGroupKey.Transactions => "Transactions",
            SearchGroupKey.Contacts => "Contacts",
            SearchGroupKey.Products => "Products",
            SearchGroupKey.Reports => "Reports",
            _ => "Results"
        };

    private static int ResolveGroupOrder(string groupKey) =>
        groupKey.Trim().ToLowerInvariant() switch
        {
            SearchGroupKey.JumpTo => 0,
            SearchGroupKey.Transactions => 1,
            SearchGroupKey.Contacts => 2,
            SearchGroupKey.Products => 3,
            SearchGroupKey.Reports => 4,
            _ => 5
        };

    /// <summary>
    /// SHA-256 of the normalized query, base16-lowercased. Stable across
    /// processes / deployments / cache regenerations so the cache row
    /// keyed by hash matches between writes (backfill) and reads
    /// (search hot path).
    /// </summary>
    private static string ComputeQueryHash(string normalizedQuery)
    {
        Span<byte> hash = stackalloc byte[32];
        var bytes = Encoding.UTF8.GetBytes(normalizedQuery);
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
