using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SessionContext;

public interface ICompanySessionContextStore
{
    Task<CompanyAccessSessionContext?> GetAsync(
        UserId userId,
        CompanyId? preferredActiveCompanyId,
        CancellationToken cancellationToken);
}
