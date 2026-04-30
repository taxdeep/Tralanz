using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Modules.UnitySearch.Application;

public sealed class UnitySearchEngine(
    UnitySearchPolicyRegistry policyRegistry,
    IUnitySearchProjectionStore projectionStore,
    IUnitySearchQueryService queryService,
    IUnitySearchStatsStore statsStore) : IUnitySearchEngine
{
    public async Task<UnitySearchResult> SearchAsync(UnitySearchQuery query, CancellationToken cancellationToken)
    {
        var normalizedQuery = UnitySearchCanonicalizer.Normalize(query.SearchText);
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

            return new UnitySearchResult
            {
                QueryText = string.Empty,
                Context = query.Context,
                RecentQueries = recentQueries,
                RecentSelections = recentSelections
            };
        }

        var policy = policyRegistry.Resolve(query.Context);
        await projectionStore.EnsureProjectionFreshAsync(query.CompanyId, cancellationToken);

        var classification = UnitySearchQueryClassifier.Classify(normalizedQuery);
        var hints = new UnitySearchQueryHints(classification.Tag, classification.NumericValue);

        var documents = await queryService.SearchDocumentsAsync(query, policy, normalizedQuery, hints, cancellationToken);
        if (query.UserId.HasValue)
        {
            await statsStore.RecordQueryAsync(query.CompanyId, query.UserId.Value, query.Context, normalizedQuery, cancellationToken);
        }

        var grouped = documents
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

        return new UnitySearchResult
        {
            QueryText = query.SearchText.Trim(),
            Context = query.Context,
            Groups = grouped,
            TotalCount = documents.Count
        };
    }

    public Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken) =>
        statsStore.ListRecentQueriesAsync(companyId, userId, context, take, cancellationToken);

    public async Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken)
    {
        await projectionStore.EnsureProjectionFreshAsync(companyId, cancellationToken);
        return await statsStore.ListRecentSelectionsAsync(companyId, userId, context, take, cancellationToken);
    }

    public Task RecordClickAsync(
        Guid companyId,
        Guid userId,
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
}
