using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public interface IFxRateSelectionService
{
    Task<FxRateSelectionData> LoadAsync(
        FxRateSelectionRequest request,
        CancellationToken cancellationToken);

    Task<FxRateResolution> UseCompanySnapshotAsync(
        CompanyId companyId,
        Guid snapshotId,
        CancellationToken cancellationToken);

    Task<FxRateResolution> UseMarketRateAsync(
        FxRateSelectionRequest request,
        Guid marketRateId,
        CancellationToken cancellationToken);

    Task<FxRateResolution> PersistManualSnapshotAsync(
        FxRateSelectionRequest request,
        decimal rate,
        CancellationToken cancellationToken);
}
