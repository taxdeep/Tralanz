using Citus.Modules.UnitySearch.Domain.Shared;

namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchQueryService
{
    Task<IReadOnlyList<SearchDocumentRecord>> SearchDocumentsAsync(
        UnitySearchQuery query,
        SearchPolicyDefinition policy,
        string normalizedQuery,
        CancellationToken cancellationToken);
}
