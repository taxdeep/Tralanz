using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Infrastructure.Fx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

/// <summary>
/// Round-trip guard for the frankfurter→system FX direction inversion
/// done in <see cref="LocalFirstRecommendedFxRateService"/>. Frankfurter
/// publishes "1 base = X quote" (e.g. 1 CAD = 0.769230769 USD); the
/// posting engine multiplies <c>baseAmount = txAmount * Rate</c>, so it
/// needs the opposite direction (1 quote = X base, i.e. 1 USD = 1.30 CAD
/// in the example). The service inverts at the boundary; these tests
/// pin that contract.
/// </summary>
public sealed class RecommendedFxRateInversionTests
{
    [Fact]
    public async Task GetAsync_LiveFrankfurter_InvertsRateBeforeReturning()
    {
        var cache = new FakeFxRateCacheRepository();
        var frankfurter = new FakeFrankfurterClient
        {
            Response = new FrankfurterRatesResponse(
                new DateOnly(2026, 5, 1),
                "CAD",
                new Dictionary<string, decimal> { ["USD"] = 0.769230769m })
        };
        var service = new LocalFirstRecommendedFxRateService(
            cache, frankfurter, NullLogger<LocalFirstRecommendedFxRateService>.Instance);

        var result = await service.GetAsync(
            new DateOnly(2026, 5, 2), "CAD", "USD", CancellationToken.None);

        Assert.NotNull(result);
        // 1 / 0.769230769 = 1.300000000... (banker rounded to 10 dp).
        Assert.Equal(1.3m, Math.Round(result!.Rate, 4));
        // 100 USD posted at this rate must convert to 130 CAD on the GL.
        Assert.Equal(130m, Math.Round(100m * result.Rate, 2));
    }

    [Fact]
    public async Task GetAsync_LiveFrankfurter_PersistsInvertedRateToCache()
    {
        var cache = new FakeFxRateCacheRepository();
        var frankfurter = new FakeFrankfurterClient
        {
            Response = new FrankfurterRatesResponse(
                new DateOnly(2026, 5, 1),
                "CAD",
                new Dictionary<string, decimal> { ["USD"] = 0.769230769m, ["EUR"] = 0.65m })
        };
        var service = new LocalFirstRecommendedFxRateService(
            cache, frankfurter, NullLogger<LocalFirstRecommendedFxRateService>.Instance);

        await service.GetAsync(new DateOnly(2026, 5, 2), "CAD", "USD", CancellationToken.None);

        Assert.Equal(2, cache.UpsertedRows.Count);
        var usdRow = cache.UpsertedRows.Single(r => r.QuoteCurrencyCode == "USD");
        Assert.Equal(1.3m, Math.Round(usdRow.Rate, 4));
        var eurRow = cache.UpsertedRows.Single(r => r.QuoteCurrencyCode == "EUR");
        // 1 / 0.65 ≈ 1.5384615
        Assert.Equal(1.5385m, Math.Round(eurRow.Rate, 4));
    }

    [Fact]
    public async Task GetAsync_CacheHit_ReturnsStoredValueWithoutSecondInversion()
    {
        var cache = new FakeFxRateCacheRepository
        {
            // Cache already in tx_to_base direction (post-fix world).
            Stored = new FxRateCacheRow(
                new DateOnly(2026, 5, 1), "CAD", "USD", 1.30m, "frankfurter")
        };
        var frankfurter = new FakeFrankfurterClient(); // never called
        var service = new LocalFirstRecommendedFxRateService(
            cache, frankfurter, NullLogger<LocalFirstRecommendedFxRateService>.Instance);

        var result = await service.GetAsync(
            new DateOnly(2026, 5, 2), "CAD", "USD", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1.30m, result!.Rate);
        Assert.Equal(0, frankfurter.CallCount);
    }

    [Fact]
    public async Task GetAsync_SameCurrency_ReturnsIdentityRate()
    {
        var service = new LocalFirstRecommendedFxRateService(
            new FakeFxRateCacheRepository(),
            new FakeFrankfurterClient(),
            NullLogger<LocalFirstRecommendedFxRateService>.Instance);

        var result = await service.GetAsync(
            new DateOnly(2026, 5, 2), "USD", "USD", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1m, result!.Rate);
    }

    private sealed class FakeFxRateCacheRepository : IFxRateCacheRepository
    {
        public FxRateCacheRow? Stored { get; set; }
        public List<FxRateCacheRow> UpsertedRows { get; } = new();

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FxRateCacheRow?> GetAsync(
            DateOnly rateDate,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            CancellationToken cancellationToken) => Task.FromResult(Stored);

        public Task<FxRateCacheRow?> GetLatestBeforeAsync(
            DateOnly upperBoundDate,
            string baseCurrencyCode,
            string quoteCurrencyCode,
            int maxLookbackDays,
            CancellationToken cancellationToken) => Task.FromResult<FxRateCacheRow?>(null);

        public Task UpsertManyAsync(
            IReadOnlyList<FxRateCacheRow> rows,
            CancellationToken cancellationToken)
        {
            UpsertedRows.AddRange(rows);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFrankfurterClient : IFrankfurterFxRateClient
    {
        public FrankfurterRatesResponse? Response { get; set; }
        public int CallCount { get; private set; }

        public Task<FrankfurterRatesResponse?> GetRatesAsync(
            DateOnly requestedDate,
            string baseCurrencyCode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Response);
        }
    }
}
