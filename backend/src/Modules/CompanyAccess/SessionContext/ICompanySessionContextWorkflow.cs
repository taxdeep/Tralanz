using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SessionContext;

public interface ICompanySessionContextWorkflow
{
    Task<CompanyAccessSessionContext?> GetAsync(
        UserId userId,
        CompanyId? preferredActiveCompanyId,
        CancellationToken cancellationToken);
}
