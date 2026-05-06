using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public interface ICompanyCurrencyGovernanceWorkflow
{
    Task<CompanyCurrencyProfile> GetProfileAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
        CompanyId companyId,
        string currencyCode,
        UserId userId,
        CancellationToken cancellationToken);
}
