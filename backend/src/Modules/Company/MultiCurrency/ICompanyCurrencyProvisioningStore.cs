using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public interface ICompanyCurrencyProvisioningStore : ICompanyCurrencyCatalog
{
    Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
        Guid companyId,
        string currencyCode,
        IReadOnlyList<ControlAccountProvisioningRequest> controlAccounts,
        CancellationToken cancellationToken);

    /// <summary>
    /// Allocates the next-free numeric account codes for AR and AP per-currency
    /// control accounts, sized to the company's account_code_length and seated
    /// in the chart's reserved 11xxx / 20xxx families. The returned codes are
    /// derived from the company's existing 11xx / 20xx accounts (max trailing
    /// integer + 1) so a company that already has "Accounts Receivable - USD"
    /// at 11001 will allocate the next currency at 11002.
    /// </summary>
    Task<CompanyControlAccountSlots> AllocateControlAccountSlotsAsync(
        Guid companyId,
        CancellationToken cancellationToken);
}

public sealed record class CompanyControlAccountSlots(
    string ArCode,
    string ApCode);
