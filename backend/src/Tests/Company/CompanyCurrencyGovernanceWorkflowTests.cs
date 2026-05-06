using Modules.Company.MultiCurrency;
using SharedKernel.Company;

namespace Tests.Company;

public sealed class CompanyCurrencyGovernanceWorkflowTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);

    [Fact]
    public async Task EnableCurrencyAsync_ProvisionsForeignControlAccounts()
    {
        var store = new StubStore();
        var workflow = new CompanyCurrencyGovernanceWorkflow(store);

        var result = await workflow.EnableCurrencyAsync(
            CompanyId,
            "cad",
            UserId.FromOrdinal(1),
            CancellationToken.None);

        Assert.Equal("CAD", store.EnabledCurrencyCode);
        Assert.Equal(2, store.ControlAccounts.Count);
        Assert.Contains(store.ControlAccounts, account => account.SystemRole == "accounts_receivable:CAD");
        Assert.Contains(store.ControlAccounts, account => account.SystemRole == "accounts_payable:CAD");
        Assert.True(result.Profile.IsCurrencyEnabled("CAD"));
    }

    [Fact]
    public async Task EnableCurrencyAsync_SkipsProvisioningForBaseCurrency()
    {
        var store = new StubStore();
        var workflow = new CompanyCurrencyGovernanceWorkflow(store);

        var result = await workflow.EnableCurrencyAsync(
            CompanyId,
            "usd",
            UserId.FromOrdinal(1),
            CancellationToken.None);

        Assert.Null(store.EnabledCurrencyCode);
        Assert.Empty(store.ControlAccounts);
        Assert.True(result.Profile.IsCurrencyEnabled("USD"));
    }

    private sealed class StubStore : ICompanyCurrencyProvisioningStore
    {
        public string? EnabledCurrencyCode { get; private set; }

        public IReadOnlyList<ControlAccountProvisioningRequest> ControlAccounts { get; private set; } = [];

        public Task<CompanyCurrencyProfile> GetProfileAsync(CompanyId companyId, CancellationToken cancellationToken) =>
            Task.FromResult(new CompanyCurrencyProfile(
                companyId,
                "Northwind Studio Ltd.",
                "USD",
                false,
                [new CompanyCurrencyOption("USD", "US Dollar", true, true)]));

        public Task<CompanyControlAccountSlots> AllocateControlAccountSlotsAsync(
            CompanyId companyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CompanyControlAccountSlots("11001", "20001"));

        public Task<CompanyCurrencyGovernanceResult> EnableCurrencyAsync(
            CompanyId companyId,
            string currencyCode,
            IReadOnlyList<ControlAccountProvisioningRequest> controlAccounts,
            CancellationToken cancellationToken)
        {
            EnabledCurrencyCode = currencyCode;
            ControlAccounts = controlAccounts;

            var currencies = new List<CompanyCurrencyOption>
            {
                new("USD", "US Dollar", true, true)
            };

            if (!string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase))
            {
                currencies.Add(new(currencyCode, $"{currencyCode} Currency", false, true));
            }

            var result = new CompanyCurrencyGovernanceResult(
                new CompanyCurrencyProfile(
                    companyId,
                    "Northwind Studio Ltd.",
                    "USD",
                    !string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase),
                    currencies),
                controlAccounts.Select(account => new ProvisionedControlAccount(
                    Guid.CreateVersion7(),
                    account.Code,
                    account.Name,
                    account.CurrencyCode,
                    account.SystemRole,
                    true)).ToArray());

            return Task.FromResult(result);
        }
    }
}
