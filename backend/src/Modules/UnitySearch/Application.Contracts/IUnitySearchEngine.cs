namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchEngine
{
    Task<UnitySearchResult> SearchAsync(UnitySearchQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitySearchRecentQueryRecord>> ListRecentQueriesAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitySearchRecentSelectionRecord>> ListRecentSelectionsAsync(
        Guid companyId,
        Guid userId,
        string context,
        int take,
        CancellationToken cancellationToken);

    Task RecordClickAsync(
        Guid companyId,
        Guid userId,
        string context,
        string entityType,
        Guid sourceId,
        CancellationToken cancellationToken);
}
