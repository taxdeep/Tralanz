using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.Company;

namespace Tests.AP;

public sealed class VendorCurrencyWorkflowTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_UpdatesCurrencyWhenVendorHasNoHistory()
    {
        var store = new StubStore(hasTransactionHistory: false);
        var workflow = new VendorCurrencyWorkflow(store, new StubCompanyCurrencyCatalog(["USD", "EUR"]));

        var result = await workflow.ChangeDefaultCurrencyAsync(
            VendorId,
            "eur",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.CurrencyChanged);
        Assert.False(result.LockPersisted);
        Assert.Equal("EUR", result.Preference.DefaultCurrencyCode);
        Assert.False(result.Preference.IsLocked);
    }

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_RejectsCurrencyOutsideCompanyGovernance()
    {
        var store = new StubStore(hasTransactionHistory: false);
        var workflow = new VendorCurrencyWorkflow(store, new StubCompanyCurrencyCatalog(["USD"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
            VendorId,
            "eur",
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Contains("not enabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeDefaultCurrencyAsync_LocksVendorOnceHistoryExists()
    {
        var store = new StubStore(hasTransactionHistory: true);
        var workflow = new VendorCurrencyWorkflow(store, new StubCompanyCurrencyCatalog(["USD", "EUR"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ChangeDefaultCurrencyAsync(
            VendorId,
            "eur",
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.Preference.CurrencyLocked);
    }

    private sealed class StubStore : IVendorCurrencyStore
    {
        public StubStore(bool hasTransactionHistory)
        {
            Preference = new VendorCurrencyPreference(
                VendorId,
                CompanyId,
                "North Harbor Supply",
                "USD",
                CurrencyLocked: false,
                HasTransactionHistory: hasTransactionHistory);
        }

        public VendorCurrencyPreference Preference { get; private set; }

        public Task<VendorCurrencyPreference> GetPreferenceAsync(
            Guid vendorId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Preference);

        public Task<VendorCurrencyPreference> SavePreferenceAsync(
            Guid vendorId,
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
            Guid companyId,
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
