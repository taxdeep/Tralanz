using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public interface ICompanyCurrencyGovernanceWorkflow
{
    Task<CompanyCurrencyProfile> GetProfileAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
        Guid companyId,
        string currencyCode,
        Guid userId,
        CancellationToken cancellationToken);
}
