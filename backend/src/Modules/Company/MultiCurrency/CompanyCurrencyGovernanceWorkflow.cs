using SharedKernel.Company;

namespace Modules.Company.MultiCurrency;

public sealed class CompanyCurrencyGovernanceWorkflow : ICompanyCurrencyGovernanceWorkflow
{
    private readonly ICompanyCurrencyProvisioningStore _store;

    public CompanyCurrencyGovernanceWorkflow(ICompanyCurrencyProvisioningStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<CompanyCurrencyProfile> GetProfileAsync(
        Guid companyId,
        CancellationToken cancellationToken) =>
        _store.GetProfileAsync(companyId, cancellationToken);

    public async Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
        Guid companyId,
        string currencyCode,
        Guid userId,
        CancellationToken cancellationToken)
    {
        _ = userId;

        var normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);
        var profile = await _store.GetProfileAsync(companyId, cancellationToken);

        if (profile.IsCurrencyEnabled(normalizedCurrencyCode))
        {
            return new CompanyCurrencyGovernanceResult(profile, []);
        }

        var controlAccounts = await BuildControlAccountsAsync(
            companyId,
            profile.BaseCurrencyCode,
            normalizedCurrencyCode,
            cancellationToken);
        return await _store.EnableCurrencyAsync(
            companyId,
            normalizedCurrencyCode,
            controlAccounts,
            cancellationToken);
    }

    private static string NormalizeCurrencyCode(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException("A currency code is required.");
        }

        return currencyCode.Trim().ToUpperInvariant();
    }

    private async Task<IReadOnlyList<ControlAccountProvisioningRequest>> BuildControlAccountsAsync(
        Guid companyId,
        string baseCurrencyCode,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        if (string.Equals(baseCurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var slots = await _store.AllocateControlAccountSlotsAsync(companyId, cancellationToken);

        return
        [
            new ControlAccountProvisioningRequest(
                Code: slots.ArCode,
                Name: $"Accounts Receivable - {currencyCode}",
                RootType: "asset",
                DetailType: "accounts_receivable",
                CurrencyCode: currencyCode,
                SystemKey: $"control_account:accounts_receivable:{currencyCode}",
                SystemRole: $"accounts_receivable:{currencyCode}",
                AllowManualPosting: false),
            new ControlAccountProvisioningRequest(
                Code: slots.ApCode,
                Name: $"Accounts Payable - {currencyCode}",
                RootType: "liability",
                DetailType: "accounts_payable",
                CurrencyCode: currencyCode,
                SystemKey: $"control_account:accounts_payable:{currencyCode}",
                SystemRole: $"accounts_payable:{currencyCode}",
                AllowManualPosting: false)
        ];
    }
}
