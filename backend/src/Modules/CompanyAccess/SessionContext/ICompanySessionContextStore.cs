using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SessionContext;

public interface ICompanySessionContextStore
{
    Task<CompanyAccessSessionContext?> GetAsync(
        Guid userId,
        Guid? preferredActiveCompanyId,
        CancellationToken cancellationToken);
}
