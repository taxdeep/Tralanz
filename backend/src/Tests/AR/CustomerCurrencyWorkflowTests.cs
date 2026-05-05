using Modules.AR.CustomerCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.Company;

namespace Tests.AR;

public sealed class CustomerCurrencyWorkflowTests
{
    private static readonly CompanyId CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid CustomerId = Guid.Parse("91000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_UpdatesCurrencyWhenCustomerHasNoHistory()
    {
        var store = new StubStore(hasTransactionHistory: false);
        var workflow = new CustomerCurrencyWorkflow(store, new StubCompanyCurrencyCatalog(["USD", "CAD"]));

        var result = await workflow.ChangeDefaultCurrencyAsync(
            CustomerId,
            "cad",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.CurrencyChanged);
        Assert.False(result.LockPersisted);
        Assert.Equal("CAD", result.Preference.DefaultCurrencyCode);
        Assert.False(result.Preference.IsLocked);
    }

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_RejectsCurrencyOutsideCompanyGovernance()
    {
        var store = new StubStore(hasTransactionHistory: false);
        var workflow = new CustomerCurrencyWorkflow(store, new StubCompanyCurrencyCatalog(["USD"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
            CustomerId,
            "eur",
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Contains("not enabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_LocksCustomerOnceHistoryExists()
    {
        var store = new StubStore(hasTransactionHistory: true);
        var workflow = new CustomerCurrencyWorkflow(store, new StubCompanyCurrencyCatalog(["USD", "CAD"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
            CustomerId,
            "cad",
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.Preference.CurrencyLocked);
    }

    private sealed class StubStore : ICustomerCurrencyStore
    {
        public StubStore(bool hasTransactionHistory)
        {
            Preference = new CustomerCurrencyPreference(
                CustomerId,
                CompanyId,
                "Acme Retail",
                "USD",
                CurrencyLocked: false,
                HasTransactionHistory: hasTransactionHistory);
        }

        public CustomerCurrencyPreference Preference { get; private set; }

        public Task<CustomerCurrencyPreference> GetPreferenceAsync(
            Guid customerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Preference);

        public Task<CustomerCurrencyPreference> SavePreferenceAsync(
            Guid customerId,
            string defaultCurrencyCode,
            bool currencyLocked,
            CancellationToken cancellationToken)
        {
            Preference = Preference with
            {
                DefaultCurrencyCode = defaultCurrencyCode,
                CurrencyLocked = currencyLocked
            };

            return Task.FromResult(Preference);
        }
    }

    private sealed class StubCompanyCurrencyCatalog : ICompanyCurrencyCatalog
    {
        private readonly IReadOnlyList<string> _enabledCurrencies;

        public StubCompanyCurrencyCatalog(IReadOnlyList<string> enabledCurrencies)
        {
            _enabledCurrencies = enabledCurrencies;
        }

        public Task<CompanyCurrencyProfile> GetProfileAsync(
            CompanyId companyId,
            CancellationToken cancellationToken)
        {
            var currencies = _enabledCurrencies
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(code => new CompanyCurrencyOption(
                    code.ToUpperInvariant(),
                    code.ToUpperInvariant(),
                    string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase),
                    true))
                .ToArray();

            return Task.FromResult(new CompanyCurrencyProfile(
                companyId,
                "Northwind Studio Ltd.",
                "USD",
                currencies.Any(currency => !currency.IsBaseCurrency),
                currencies));
        }
    }
}
