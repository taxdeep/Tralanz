using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public sealed class FxRateSelectionService : IFxRateSelectionService
{
    private readonly IFxRateStore _store;

    public FxRateSelectionService(IFxRateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<FxRateSelectionData> LoadAsync(
        FxRateSelectionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshots = await _store.ListCompanySnapshotsAsync(
            request.CompanyId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            request.Take,
            cancellationToken);

        var marketRates = await _store.ListMarketRatesAsync(
            request.ProviderKey,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            request.Take,
            cancellationToken);

        return new FxRateSelectionData(snapshots, marketRates);
    }

    public async Task<FxRateResolution> UseCompanySnapshotAsync(
        CompanyId companyId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _store.FindCompanySnapshotByIdAsync(companyId, snapshotId, cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException("The selected FX snapshot was not found.");
        }

        return ToResolution(snapshot);
    }

    public async Task<FxRateResolution> UseMarketRateAsync(
        FxRateSelectionRequest request,
        Guid marketRateId,
        CancellationToken cancellationToken)
    {
        var marketRate = await _store.FindMarketRateByIdAsync(marketRateId, cancellationToken);
        if (marketRate is null)
        {
            throw new InvalidOperationException("The selected market FX rate was not found.");
        }

        var snapshot = await _store.UpsertCompanySnapshotAsync(
            request.CompanyId,
            request.CreatedByUserId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            marketRate with
            {
                RateType = request.RateType,
                QuoteBasis = request.QuoteBasis
            },
            request.ProviderKey,
            request.RateType,
            request.QuoteBasis,
            request.RateUseCase,
            request.PostingReason,
            cancellationToken);

        return ToResolution(snapshot);
    }

    public async Task<FxRateResolution> PersistManualSnapshotAsync(
        FxRateSelectionRequest request,
        decimal rate,
        CancellationToken cancellationToken)
    {
        if (rate <= 0m)
        {
            throw new InvalidOperationException("Manual FX rate must be greater than zero.");
        }

        var snapshot = await _store.CreateManualCompanySnapshotAsync(
            request.CompanyId,
            request.CreatedByUserId,
            request.BaseCurrencyCode,
            request.QuoteCurrencyCode,
            request.RequestedDate,
            rate,
            request.ProviderKey,
            request.RateType,
            request.QuoteBasis,
            request.RateUseCase,
            request.PostingReason,
            cancellationToken);

        return ToResolution(snapshot);
    }

    private static FxRateResolution ToResolution(FxSnapshotRecord snapshot)
    {
        var statusLabel = snapshot.SnapshotSemantics == FxSourceSemantics.Manual
            ? "Manual company snapshot"
            : snapshot.EffectiveDate == snapshot.RequestedDate
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
