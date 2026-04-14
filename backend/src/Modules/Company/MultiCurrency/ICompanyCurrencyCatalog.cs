using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public interface ICompanyCurrencyCatalog
{
    Task<CompanyCurrencyProfile> GetProfileAsync(
        Guid companyId,
        CancellationToken cancellationToken);
}
