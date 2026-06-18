using SharedKernel.Identity;

namespace Citus.Accounting.Application.Companies;

/// <summary>
/// Reads and writes the per-company money decimal precision
/// (<c>companies.money_decimals</c>). The UI read path flows through the
/// session-summary projection; the posting pipeline reads via <see cref="GetAsync"/>
/// so the fragment builder / tax engine can round to the company precision.
/// </summary>
public interface ICompanyMoneyDecimalsStore
{
    Task SetAsync(CompanyId companyId, int moneyDecimals, CancellationToken cancellationToken);

    /// <summary>Returns the company's money decimals (2 or 3); falls back to 2.</summary>
    Task<int> GetAsync(CompanyId companyId, CancellationToken cancellationToken);
}
