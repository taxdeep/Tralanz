using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public sealed record class CompanyCurrencyGovernanceResult(
    CompanyCurrencyProfile Profile,
    IReadOnlyList<ProvisionedControlAccount> ProvisionedControlAccounts);
