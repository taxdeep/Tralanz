using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public sealed class FxRateResolver : IFxRateResolver
{
    private readonly IFxRateStore _store;
    private readonly IFxProviderClient _providerClient;

    public FxRateResolver(
        IFxRateStore store,
        IFxProviderClient providerClient)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
    }

    public async Task<FxRateResolution> ResolveAsync(
        FxRateLookupRequest request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                request.BaseCurrencyCode,
                request.QuoteCurrencyCode,
                StringComparison.OrdinalIgnoreCase))
        {
            return FxRateResolution.Identity(request.RequestedDate);
        }

        var companySnapshot = await _store.FindLatestCompanySnapshotAsync(
            request.CompanyId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            request.ProviderKey,
            request.LookbackDays,
            request.RateType,
            request.QuoteBasis,
            request.RateUseCase,
            cancellationToken);

        if (companySnapshot is not null)
        {
            return ToResolution(companySnapshot);
        }

        var marketRate = await _store.FindLatestMarketRateAsync(
            request.ProviderKey,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            request.LookbackDays,
            request.RateType,
            request.QuoteBasis,
            cancellationToken);

        if (marketRate is not null)
        {
        var promotedSnapshot = await _store.UpsertCompanySnapshotAsync(
            request.CompanyId,
            request.CreatedByUserId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            marketRate,
            request.ProviderKey,
            request.RateType,
            request.QuoteBasis,
            request.RateUseCase,
            request.PostingReason,
            cancellationToken);

            return ToResolution(promotedSnapshot);
        }

        var remoteRows = await _providerClient.GetRatesAsync(
            request.ProviderKey,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate.AddDays(-request.LookbackDays),
            request.RequestedDate,
            cancellationToken);

        if (remoteRows.Count == 0)
        {
            throw new InvalidOperationException(
                $"No FX rate found for {request.BaseCurrencyCode}/{request.QuoteCurrencyCode} on or before {request.RequestedDate:yyyy-MM-dd}.");
        }

        var storedRates = await _store.UpsertMarketRatesAsync(remoteRows, cancellationToken);

        var selectedRate = storedRates
            .Where(row => row.MarketDate <= request.RequestedDate)
            .OrderByDescending(row => row.MarketDate)
            .FirstOrDefault();

        if (selectedRate is null)
        {
            throw new InvalidOperationException(
                $"No usable FX rate was returned for {request.BaseCurrencyCode}/{request.QuoteCurrencyCode}.");
        }

        var storedSnapshot = await _store.UpsertCompanySnapshotAsync(
            request.CompanyId,
            request.CreatedByUserId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            selectedRate,
            request.ProviderKey,
            request.RateType,
            request.QuoteBasis,
            request.RateUseCase,
            request.PostingReason,
            cancellationToken);

        return ToResolution(storedSnapshot);
    }

    private static FxRateResolution ToResolution(FxSnapshotRecord snapshot)
    {
        var statusLabel = snapshot.EffectiveDate == snapshot.RequestedDate
            ? $"Local {snapshot.ProviderKey ?? "FX"}"
            : $"Local fallback {snapshot.EffectiveDate:yyyy-MM-dd}";

        return new FxRateResolution(
            snapshot.Rate,
            snapshot.RequestedDate,
            snapshot.EffectiveDate,
            snapshot.SnapshotSemantics,
            statusLabel,
            snapshot.RateType,
            snapshot.QuoteBasis,
            snapshot.RateUseCase,
            snapshot.PostingReason,
            snapshot.ProviderKey,
            snapshot.Id);
    }
}
