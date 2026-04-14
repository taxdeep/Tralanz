using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public interface ICompanyCurrencyProvisioningStore : ICompanyCurrencyCatalog
{
    Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
        Guid companyId,
        string currencyCode,
        IReadOnlyList<ControlAccountProvisioningRequest> controlAccounts,
        CancellationToken cancellationToken);
}
