using SharedKernel.CompanyAccess;

namespace Modules.CompanyAccess.SessionContext;

public interface ICompanySessionContextWorkflow
{
    Task<CompanyAccessSessionContext?> GetAsync(
        Guid userId,
        Guid? preferredActiveCompanyId,
        CancellationToken cancellationToken);
}
