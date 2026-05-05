using Modules.AP.VendorCreditApplication;
using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.Company;

namespace Tests.AP;

public sealed class VendorCreditApplicationDraftPreparationWorkflowTests
{
    private static readonly CompanyId CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrepareDraftAsync_UsesVendorCurrencyWhenNoOverrideProvided()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var store = new StubStore
        {
            Candidates =
            [
                CreateCandidate(sourceId, "vendor_credit", "debit", "USD"),
                CreateCandidate(targetId, "bill", "credit", "USD")
            ]
        };

        var workflow = new VendorCreditApplicationDraftPreparationWorkflow(
            store,
            new StubVendorCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("USD", ["USD"]));

        var result = await workflow.PrepareDraftAsync(
            new VendorCreditApplicationDraftContext(
                CompanyId,
                Guid.NewGuid(),
                VendorId,
                new DateOnly(2026, 4, 14),
                null,
                null),
            [new VendorCreditApplicationDraftLine(sourceId, targetId, 100m)],
            CancellationToken.None);

        Assert.Equal("USD", result.DocumentCurrencyCode);
        Assert.Equal("USD", store.LastPreparation?.DocumentCurrencyCode);
    }

    [Fact]
    public async Task PrepareDraftAsync_RejectsCurrencyOverrideWhenLocked()
    {
        var store = new StubStore();
        var workflow = new VendorCreditApplicationDraftPreparationWorkflow(
            store,
            new StubVendorCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("USD", ["USD", "EUR"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareDraftAsync(
            new VendorCreditApplicationDraftContext(
                CompanyId,
                Guid.NewGuid(),
                VendorId,
                new DateOnly(2026, 4, 14),
                "EUR",
                null),
            [new VendorCreditApplicationDraftLine(Guid.NewGuid(), Guid.NewGuid(), 100m)],
            CancellationToken.None));

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareDraftAsync_RejectsCrossCurrencyApplicationTargetsInPhaseOne()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var store = new StubStore
        {
            Candidates =
            [
                CreateCandidate(sourceId, "vendor_credit", "debit", "USD"),
                CreateCandidate(targetId, "bill", "credit", "USD")
            ]
        };

        var workflow = new VendorCreditApplicationDraftPreparationWorkflow(
            store,
            new StubVendorCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("CAD", ["CAD", "USD", "EUR"]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareDraftAsync(
            new VendorCreditApplicationDraftContext(
                CompanyId,
                Guid.NewGuid(),
                VendorId,
                new DateOnly(2026, 4, 14),
                null,
                null),
            [new VendorCreditApplicationDraftLine(Guid.NewGuid(), Guid.NewGuid(), 100m)],
            CancellationToken.None));

        Assert.Contains("same-currency application", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static VendorCreditApplicationOpenItemCandidate CreateCandidate(
        Guid openItemId,
        string sourceType,
        string balanceSide,
        string currencyCode) =>
        new(
            openItemId,
            VendorId,
            sourceType,
            Guid.NewGuid(),
            sourceType == "bill" ? "BILL-000001" : "VC-000001",
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 5, 1),
            currencyCode,
            "CAD",
            100m,
            100m,
            137.89m,
            balanceSide,
            "open");

    private sealed class StubStore : IVendorCreditApplicationDraftPreparationStore
    {
        public VendorCreditApplicationDraftPreparation? LastPreparation { get; private set; }

        public IReadOnlyList<VendorCreditApplicationOpenItemCandidate> Candidates { get; set; } = [];

        public Task<IReadOnlyList<VendorCreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
            CompanyId companyId,
            Guid vendorId,
            string documentCurrencyCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(Candidates.Where(candidate =>
                string.Equals(candidate.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase)).ToArray()
                as IReadOnlyList<VendorCreditApplicationOpenItemCandidate>);

        public Task<VendorCreditApplicationDraftResult> PrepareDraftAsync(
            VendorCreditApplicationDraftPreparation preparation,
            CancellationToken cancellationToken)
        {
            LastPreparation = preparation;
            return Task.FromResult(new VendorCreditApplicationDraftResult(
                Guid.NewGuid(),
                "EN202600000001",
                "VCA-000001",
                preparation.DocumentCurrencyCode,
                preparation.BaseCurrencyCode,
                100m,
                0m,
                preparation.Lines.Count,
                "draft"));
        }
    }

    private sealed class StubVendorCurrencyWorkflow : IVendorCurrencyWorkflow
    {
        private readonly string _currencyCode;

        public StubVendorCurrencyWorkflow(string currencyCode)
        {
            _currencyCode = currencyCode;
        }

        public Task<VendorCurrencyPreference> GetPreferenceAsync(
            Guid vendorId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new VendorCurrencyPreference(
                vendorId,
                CompanyId,
                "North Harbor Supply",
                _currencyCode,
                CurrencyLocked: true,
                HasTransactionHistory: true));

        public Task<VendorCurrencyChangeResult> ChangeDefaultCurrencyAsync(
            Guid vendorId,
            string currencyCode,
            UserId userId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCompanyCurrencyCatalog : ICompanyCurrencyCatalog
    {
        private readonly string _baseCurrency;
        private readonly IReadOnlyList<string> _enabledCurrencies;

        public StubCompanyCurrencyCatalog(string baseCurrency, IReadOnlyList<string> enabledCurrencies)
        {
            _baseCurrency = baseCurrency;
            _enabledCurrencies = enabledCurrencies;
        }

        public Task<CompanyCurrencyProfile> GetProfileAsync(
            CompanyId companyId,
            CancellationToken cancellationToken)
        {
            var currencies = _enabledCurrencies
                .Select(code => new CompanyCurrencyOption(
                    code,
                    code,
                    string.Equals(code, _baseCurrency, StringComparison.OrdinalIgnoreCase),
                    true))
                .ToArray();

            return Task.FromResult(new CompanyCurrencyProfile(
                companyId,
                "Northwind Studio Ltd.",
                _baseCurrency,
                currencies.Any(currency => !currency.IsBaseCurrency),
                currencies));
        }
    }
}
