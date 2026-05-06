namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchEngine
{
    Task<UnitySearchResult> SearchAsync(UnitySearchQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        CompanyId companyId,
        UserId userId,
        string context,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(
        CompanyId companyId,
        UserId userId,
        string context,
        int take,
        CancellationToken cancellationToken);

    Task RecordClickAsync(
        CompanyId companyId,
        UserId userId,
        string context,
        string entityType,
        Guid sourceId,
        CancellationToken cancellationToken);
}
