using Engines.FX.FxRateLookup;
using Modules.AP.PayBill;
using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.Company;
using SharedKernel.FX;

namespace Tests.AP;

public sealed class PayBillDraftPreparationWorkflowTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrepareDraftAsync_UsesVendorCurrencyWhenNoOverrideProvided()
    {
        var targetOpenItemId = Guid.NewGuid();
        var store = new StubStore();
        store.Candidates =
        [
            CreateCandidate(targetOpenItemId, "USD")
        ];
        var workflow = new PayBillDraftPreparationWorkflow(
            store,
            new StubVendorCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("USD", ["USD"]),
            new StubFxRateResolver(),
            new StubFxRateStore());

        var result = await workflow.PrepareDraftAsync(
            new PayBillDraftContext(
                CompanyId,
                Guid.NewGuid(),
                VendorId,
                Guid.NewGuid(),
                new DateOnly(2026, 4, 14),
                null,
                null,
                null),
            [new PayBillDraftLine(targetOpenItemId, 100m)],
            CancellationToken.None);

        Assert.Equal("USD", result.DocumentCurrencyCode);
        Assert.Equal("USD", store.LastPreparation?.DocumentCurrencyCode);
    }

    [Fact]
    public async Task PrepareDraftAsync_RejectsCurrencyOverrideWhenLocked()
    {
        var store = new StubStore();
        var workflow = new PayBillDraftPreparationWorkflow(
            store,
            new StubVendorCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("USD", ["USD", "EUR"]),
            new StubFxRateResolver(),
            new StubFxRateStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareDraftAsync(
            new PayBillDraftContext(
                CompanyId,
                Guid.NewGuid(),
                VendorId,
                Guid.NewGuid(),
                new DateOnly(2026, 4, 14),
                "EUR",
                null,
                null),
            [new PayBillDraftLine(Guid.NewGuid(), 100m)],
            CancellationToken.None));

        Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareDraftAsync_RejectsCrossCurrencySettlementTargetsInPhaseOne()
    {
        var store = new StubStore();
        store.Candidates =
        [
            CreateCandidate(Guid.NewGuid(), "USD")
        ];

        var workflow = new PayBillDraftPreparationWorkflow(
            store,
            new StubVendorCurrencyWorkflow("USD"),
            new StubCompanyCurrencyCatalog("CAD", ["CAD", "USD", "EUR"]),
            new StubFxRateResolver(),
            new StubFxRateStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PrepareDraftAsync(
            new PayBillDraftContext(
                CompanyId,
                Guid.NewGuid(),
                VendorId,
                Guid.NewGuid(),
                new DateOnly(2026, 4, 14),
                null,
                null,
                null),
            [new PayBillDraftLine(Guid.NewGuid(), 100m)],
            CancellationToken.None));

        Assert.Contains("same-currency settlement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PayBillOpenItemCandidate CreateCandidate(Guid openItemId, string currencyCode) =>
        new(
            openItemId,
            "bill",
            Guid.NewGuid(),
            "BILL-000001",
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 5, 1),
            currencyCode,
            "CAD",
            100m,
            100m,
            137.89m,
            "credit",
            "open");

    private sealed class StubStore : IPayBillDraftPreparationStore
    {
        public PayBillDraftPreparation? LastPreparation { get; private set; }
        public IReadOnlyList<PayBillOpenItemCandidate> Candidates { get; set; } = [];

        public Task<IReadOnlyList<PayBillOpenItemCandidate>> ListOpenItemCandidatesAsync(
            CompanyId companyId,
            Guid vendorId,
            string documentCurrencyCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(Candidates.Where(candidate =>
                string.Equals(candidate.DocumentCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase)).ToArray()
                as IReadOnlyList<PayBillOpenItemCandidate>);

        public Task<PayBillDraftResult> PrepareDraftAsync(
            PayBillDraftPreparation preparation,
            CancellationToken cancellationToken)
        {
            LastPreparation = preparation;
            return Task.FromResult(new PayBillDraftResult(
                Guid.NewGuid(),
                "EN202600000001",
                "PB-000001",
                preparation.DocumentCurrencyCode,
                preparation.BaseCurrencyCode,
                preparation.FxResolution.SnapshotId,
                preparation.FxResolution.Rate,
                preparation.FxResolution.RequestedDate,
                preparation.FxResolution.EffectiveDate,
                preparation.FxResolution.SourceSemantics,
                100m,
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

    private sealed class StubFxRateResolver : IFxRateResolver
    {
        public Task<FxRateResolution> ResolveAsync(
            FxRateLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(FxRateResolution.Identity(request.RequestedDate));
    }

    private sealed class StubFxRateStore : IFxRateStore
    {
        public Task<IReadOnlyList<FxSnapshotRecord>> ListCompanySnapshotsAsync(
            CompanyId companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FxSnapshotRecord>>([]);

        public Task<IReadOnlyList<FxMarketRateRecord>> ListMarketRatesAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FxMarketRateRecord>>([]);

        public Task<FxSnapshotRecord?> FindCompanySnapshotByIdAsync(
            CompanyId companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) => Task.FromResult<FxSnapshotRecord?>(null);

        public Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
            Guid marketRateId,
            CancellationToken cancellationToken) => Task.FromResult<FxMarketRateRecord?>(null);

        public Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
            CompanyId companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            string providerKey,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            CancellationToken cancellationToken) => Task.FromResult<FxSnapshotRecord?>(null);

        public Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            CancellationToken cancellationToken) => Task.FromResult<FxMarketRateRecord?>(null);

        public Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
            IReadOnlyList<FxMarketRateRecord> marketRates,
            CancellationToken cancellationToken) => Task.FromResult(marketRates);

        public Task<FxSnapshotRecord> UpsertCompanySnapshotAsync(
            CompanyId companyId,
            UserId? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            FxMarketRateRecord marketRate,
            string providerKey,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            string postingReason,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxSnapshotRecord(
                Guid.NewGuid(),
                companyId,
                baseCurrencyCode,
                quoteCurrencyCode,
                requestedDate,
                marketRate.MarketDate,
                marketRate.Rate,
                rateType,
                quoteBasis,
                rateUseCase,
                postingReason,
                providerKey,
                FxSourceSemantics.ProviderFetched,
                FxSourceSemantics.SystemStored,
                marketRate.Id,
                DateTimeOffset.UtcNow));

        public Task<FxSnapshotRecord> CreateManualCompanySnapshotAsync(
            CompanyId companyId,
            UserId? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            decimal rate,
            string providerKey,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            string postingReason,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxSnapshotRecord(
                Guid.NewGuid(),
                companyId,
                baseCurrencyCode,
                quoteCurrencyCode,
                requestedDate,
                requestedDate,
                rate,
                rateType,
                quoteBasis,
                rateUseCase,
                postingReason,
                providerKey,
                FxSourceSemantics.Manual,
                FxSourceSemantics.Manual,
                null,
                DateTimeOffset.UtcNow));
    }
}
