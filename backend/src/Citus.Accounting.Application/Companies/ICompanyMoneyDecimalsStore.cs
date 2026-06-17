using SharedKernel.Identity;

namespace Citus.Accounting.Application.Companies;

/// <summary>
/// Writes the per-company money decimal precision (<c>companies.money_decimals</c>).
/// Read access flows through the session-summary projection, so this store only
/// needs the write path.
/// </summary>
public interface ICompanyMoneyDecimalsStore
{
    Task SetAsync(CompanyId companyId, int moneyDecimals, CancellationToken cancellationToken);
}
