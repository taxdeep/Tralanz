namespace Citus.Modules.UnitySearch.Application.Contracts;

public interface IUnitySearchProjectionStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task EnsureProjectionFreshAsync(Guid companyId, CancellationToken cancellationToken);
}
