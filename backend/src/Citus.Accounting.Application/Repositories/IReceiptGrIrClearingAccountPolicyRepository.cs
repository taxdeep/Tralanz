using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

public interface IReceiptGrIrClearingAccountPolicyRepository
{
    Task<Guid?> GetDefaultGrIrClearingAccountIdAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task SaveDefaultGrIrClearingAccountAsync(
        CompanyId companyId,
        UserId userId,
        Guid grIrClearingAccountId,
        CancellationToken cancellationToken);
}
