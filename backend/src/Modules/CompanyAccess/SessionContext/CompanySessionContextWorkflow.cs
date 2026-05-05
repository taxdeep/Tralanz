using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SessionContext;

public sealed class CompanySessionContextWorkflow : ICompanySessionContextWorkflow
{
    private readonly ICompanySessionContextStore _store;

    public CompanySessionContextWorkflow(ICompanySessionContextStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<CompanyAccessSessionContext?> GetAsync(
        UserId userId,
        CompanyId? preferredActiveCompanyId,
        CancellationToken cancellationToken) =>
        _store.GetAsync(userId, preferredActiveCompanyId, cancellationToken);
}
