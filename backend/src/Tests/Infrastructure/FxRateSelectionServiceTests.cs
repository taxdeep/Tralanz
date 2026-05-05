using Engines.FX.FxRateLookup;
using SharedKernel.FX;

namespace Tests.Infrastructure;

public sealed class FxRateSelectionServiceTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);

    [Fact]
    public async Task LoadAsync_ReturnsLocalSnapshotsAndMarketRates()
    {
        var snapshot = new FxSnapshotRecord(
            Guid.NewGuid(),
            CompanyId,
            "USD",
            "CAD",
            new DateOnly(2026, 4, 13),
            new DateOnly(2026, 4, 13),
            1.3845m,
            FxRateType.Spot,
            FxQuoteBasis.Direct,
            FxRateUseCase.General,
            FxPostingReason.Normal,
            "ECB",
            "provider_fetched",
            FxSourceSemantics.SystemStored,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
        var marketRate = new FxMarketRateRecord(
            Guid.NewGuid(),
            "ECB",
            "USD",
            "CAD",
            new DateOnly(2026, 4, 12),
            1.3811m,
            FxRateType.Spot,
            FxQuoteBasis.Direct,
            DateTimeOffset.UtcNow,
            "{}");
        var store = new FakeSelectionStore(snapshot, marketRate);
        var service = new FxRateSelectionService(store);

        var result = await service.LoadAsync(
            new FxRateSelectionRequest(CompanyId, null, "USD", "CAD", new DateOnly(2026, 4, 13), "ECB", 7, FxRateType.Spot, FxQuoteBasis.Direct, FxRateUseCase.General, FxPostingReason.Normal),
            CancellationToken.None);

        Assert.Single(result.CompanySnapshots);
        Assert.Single(result.MarketRates);
    }

    [Fact]
    public async Task PersistManualSnapshotAsync_CreatesManualResolution()
    {
        var store = new FakeSelectionStore(null, null);
        var service = new FxRateSelectionService(store);

        var resolution = await service.PersistManualSnapshotAsync(
            new FxRateSelectionRequest(CompanyId, UserId.FromOrdinal(1), "USD", "CAD", new DateOnly(2026, 4, 13), "ECB", 7, FxRateType.Spot, FxQuoteBasis.Direct, FxRateUseCase.General, FxPostingReason.Normal),
            1.3999m,
            CancellationToken.None);

        Assert.Equal(FxSourceSemantics.Manual, resolution.SourceSemantics);
        Assert.NotNull(resolution.SnapshotId);
        Assert.Equal("Manual company snapshot", resolution.StatusLabel);
    }

    private sealed class FakeSelectionStore : IFxRateStore
    {
        private readonly FxSnapshotRecord? _snapshot;
        private readonly FxMarketRateRecord? _marketRate;

        public FakeSelectionStore(FxSnapshotRecord? snapshot, FxMarketRateRecord? marketRate)
        {
            _snapshot = snapshot;
            _marketRate = marketRate;
        }

        public Task<IReadOnlyList<FxSnapshotRecord>> ListCompanySnapshotsAsync(
            CompanyId companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FxSnapshotRecord>>(_snapshot is null ? [] : [_snapshot]);

        public Task<IReadOnlyList<FxMarketRateRecord>> ListMarketRatesAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FxMarketRateRecord>>(_marketRate is null ? [] : [_marketRate]);

        public Task<FxSnapshotRecord?> FindCompanySnapshotByIdAsync(
            CompanyId companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot is not null && _snapshot.Id == snapshotId ? _snapshot : null);

        public Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
            Guid marketRateId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_marketRate is not null && _marketRate.Id == marketRateId ? _marketRate : null);

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
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);

        public Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            CancellationToken cancellationToken) =>
            Task.FromResult(_marketRate);

        public Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
            IReadOnlyList<FxMarketRateRecord> marketRates,
            CancellationToken cancellationToken) =>
            Task.FromResult(marketRates);

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
                "provider_fetched",
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
                "manual",
                FxSourceSemantics.Manual,
                null,
                DateTimeOffset.UtcNow));
    }
}
