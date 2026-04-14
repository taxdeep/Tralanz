using Engines.FX.FxRateLookup;
using SharedKernel.FX;

namespace Tests.Infrastructure;

public sealed class FxRateSelectionServiceTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");

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
            DateTimeOffset.UtcNow,
            "{}");
        var store = new FakeSelectionStore(snapshot, marketRate);
        var service = new FxRateSelectionService(store);

        var result = await service.LoadAsync(
            new FxRateSelectionRequest(CompanyId, null, "USD", "CAD", new DateOnly(2026, 4, 13), "ECB", 7),
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
            new FxRateSelectionRequest(CompanyId, Guid.NewGuid(), "USD", "CAD", new DateOnly(2026, 4, 13), "ECB", 7),
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
            Guid companyId,
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
            Guid companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot is not null && _snapshot.Id == snapshotId ? _snapshot : null);

        public Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
            Guid marketRateId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_marketRate is not null && _marketRate.Id == marketRateId ? _marketRate : null);

        public Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
            Guid companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            string providerKey,
            int lookbackDays,
            CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);

        public Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int lookbackDays,
            CancellationToken cancellationToken) =>
            Task.FromResult(_marketRate);

        public Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
            IReadOnlyList<FxMarketRateRecord> marketRates,
            CancellationToken cancellationToken) =>
            Task.FromResult(marketRates);

        public Task<FxSnapshotRecord> UpsertCompanySnapshotAsync(
            Guid companyId,
            Guid? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            FxMarketRateRecord marketRate,
            string providerKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxSnapshotRecord(
                Guid.NewGuid(),
                companyId,
                baseCurrencyCode,
                quoteCurrencyCode,
                requestedDate,
                marketRate.MarketDate,
                marketRate.Rate,
                providerKey,
                "provider_fetched",
                FxSourceSemantics.SystemStored,
                marketRate.Id,
                DateTimeOffset.UtcNow));

        public Task<FxSnapshotRecord> CreateManualCompanySnapshotAsync(
            Guid companyId,
            Guid? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            decimal rate,
            string providerKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FxSnapshotRecord(
                Guid.NewGuid(),
                companyId,
                baseCurrencyCode,
                quoteCurrencyCode,
                requestedDate,
                requestedDate,
                rate,
                providerKey,
                "manual",
                FxSourceSemantics.Manual,
                null,
                DateTimeOffset.UtcNow));
    }
}
