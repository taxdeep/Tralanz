using Engines.FX.FxRateLookup;
using SharedKernel.FX;

namespace Tests.Infrastructure;

public sealed class FxRateResolverTests
{
    private static readonly Guid CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");

    [Fact]
    public async Task ResolveAsync_ReturnsIdentity_WhenBaseMatchesQuote()
    {
        var store = new FakeFxRateStore();
        var client = new FakeFxProviderClient();
        var resolver = new FxRateResolver(store, client);

        var resolution = await resolver.ResolveAsync(
            new FxRateLookupRequest(
                CompanyId,
                null,
                "USD",
                "USD",
                new DateOnly(2026, 4, 13),
                "ECB",
                7,
                FxRateType.Spot,
                FxQuoteBasis.Direct,
                FxRateUseCase.General,
                FxPostingReason.Normal),
            CancellationToken.None);

        Assert.Equal(1m, resolution.Rate);
        Assert.Equal(FxSourceSemantics.Identity, resolution.SourceSemantics);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task ResolveAsync_UsesLocalCompanySnapshot_BeforeProvider()
    {
        var store = new FakeFxRateStore
        {
            CompanySnapshot = new FxSnapshotRecord(
                Guid.NewGuid(),
                CompanyId,
                "USD",
                "CAD",
                new DateOnly(2026, 4, 13),
                new DateOnly(2026, 4, 10),
                1.3822m,
                FxRateType.Spot,
                FxQuoteBasis.Direct,
                FxRateUseCase.General,
                FxPostingReason.Normal,
                "ECB",
                "provider_fetched",
                FxSourceSemantics.SystemStored,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow)
        };

        var client = new FakeFxProviderClient();
        var resolver = new FxRateResolver(store, client);

        var resolution = await resolver.ResolveAsync(
            new FxRateLookupRequest(
                CompanyId,
                null,
                "USD",
                "CAD",
                new DateOnly(2026, 4, 13),
                "ECB",
                7,
                FxRateType.Spot,
                FxQuoteBasis.Direct,
                FxRateUseCase.General,
                FxPostingReason.Normal),
            CancellationToken.None);

        Assert.Equal(1.3822m, resolution.Rate);
        Assert.Equal(FxSourceSemantics.SystemStored, resolution.SourceSemantics);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task ResolveAsync_FetchesRemoteRows_AndPromotesToCompanySnapshot_WhenLocalIsMissing()
    {
        var store = new FakeFxRateStore();
        var client = new FakeFxProviderClient(
            new FxMarketRateRecord(Guid.Empty, "ECB", "USD", "CAD", new DateOnly(2026, 4, 7), 1.3912m, FxRateType.Spot, FxQuoteBasis.Direct, DateTimeOffset.UtcNow, """{"date":"2026-04-07"}"""),
            new FxMarketRateRecord(Guid.Empty, "ECB", "USD", "CAD", new DateOnly(2026, 4, 10), 1.3822m, FxRateType.Spot, FxQuoteBasis.Direct, DateTimeOffset.UtcNow, """{"date":"2026-04-10"}"""));
        var resolver = new FxRateResolver(store, client);

        var resolution = await resolver.ResolveAsync(
            new FxRateLookupRequest(
                CompanyId,
                null,
                "USD",
                "CAD",
                new DateOnly(2026, 4, 13),
                "ECB",
                7,
                FxRateType.Spot,
                FxQuoteBasis.Direct,
                FxRateUseCase.General,
                FxPostingReason.Normal),
            CancellationToken.None);

        Assert.Equal(1.3822m, resolution.Rate);
        Assert.Equal(FxSourceSemantics.SystemStored, resolution.SourceSemantics);
        Assert.Single(client.Requests);
        Assert.Equal(new DateOnly(2026, 4, 13), store.PromotedSnapshot!.RequestedDate);
        Assert.Equal(new DateOnly(2026, 4, 10), store.PromotedSnapshot.EffectiveDate);
        Assert.Equal(2, store.StoredMarketRates.Count);
    }

    private sealed class FakeFxRateStore : IFxRateStore
    {
        public FxSnapshotRecord? CompanySnapshot { get; set; }

        public FxMarketRateRecord? MarketRate { get; set; }

        public List<FxMarketRateRecord> StoredMarketRates { get; } = [];

        public FxSnapshotRecord? PromotedSnapshot { get; private set; }

        public Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
            Guid companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            string providerKey,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            CancellationToken cancellationToken)
            => Task.FromResult(CompanySnapshot);

        public Task<IReadOnlyList<FxSnapshotRecord>> ListCompanySnapshotsAsync(
            Guid companyId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FxSnapshotRecord>>(CompanySnapshot is null ? [] : [CompanySnapshot]);

        public Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int lookbackDays,
            string rateType,
            string quoteBasis,
            CancellationToken cancellationToken)
            => Task.FromResult(MarketRate);

        public Task<IReadOnlyList<FxMarketRateRecord>> ListMarketRatesAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FxMarketRateRecord>>(MarketRate is null ? StoredMarketRates : [MarketRate]);

        public Task<FxSnapshotRecord?> FindCompanySnapshotByIdAsync(
            Guid companyId,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(CompanySnapshot is not null && CompanySnapshot.Id == snapshotId ? CompanySnapshot : PromotedSnapshot);

        public Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
            Guid marketRateId,
            CancellationToken cancellationToken) =>
            Task.FromResult(StoredMarketRates.FirstOrDefault(x => x.Id == marketRateId));

        public Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
            IReadOnlyList<FxMarketRateRecord> marketRates,
            CancellationToken cancellationToken)
        {
            StoredMarketRates.AddRange(marketRates.Select((row, index) => row with { Id = Guid.NewGuid() }));
            return Task.FromResult<IReadOnlyList<FxMarketRateRecord>>(StoredMarketRates);
        }

        public Task<FxSnapshotRecord> UpsertCompanySnapshotAsync(
            Guid companyId,
            Guid? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            FxMarketRateRecord marketRate,
            string providerKey,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            string postingReason,
            CancellationToken cancellationToken)
        {
            PromotedSnapshot = new FxSnapshotRecord(
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
                DateTimeOffset.UtcNow);

            return Task.FromResult(PromotedSnapshot);
        }

        public Task<FxSnapshotRecord> CreateManualCompanySnapshotAsync(
            Guid companyId,
            Guid? createdByUserId,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly requestedDate,
            decimal rate,
            string providerKey,
            string rateType,
            string quoteBasis,
            string rateUseCase,
            string postingReason,
            CancellationToken cancellationToken)
        {
            PromotedSnapshot = new FxSnapshotRecord(
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
                DateTimeOffset.UtcNow);

            return Task.FromResult(PromotedSnapshot);
        }
    }

    private sealed class FakeFxProviderClient(params FxMarketRateRecord[] rows) : IFxProviderClient
    {
        public List<(DateOnly FromDate, DateOnly ToDate)> Requests { get; } = [];

        public Task<IReadOnlyList<FxMarketRateRecord>> GetRatesAsync(
            string providerKey,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
        {
            Requests.Add((fromDate, toDate));
            return Task.FromResult<IReadOnlyList<FxMarketRateRecord>>(rows);
        }
    }
}
