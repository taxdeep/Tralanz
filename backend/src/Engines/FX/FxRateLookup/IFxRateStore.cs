using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public interface IFxRateStore
{
    Task<IReadOnlyList<FxSnapshotRecord>> ListCompanySnapshotsAsync(
        CompanyId companyId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FxMarketRateRecord>> ListMarketRatesAsync(
        string providerKey,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        int take,
        CancellationToken cancellationToken);

    Task<FxSnapshotRecord?> FindCompanySnapshotByIdAsync(
        CompanyId companyId,
        Guid snapshotId,
        CancellationToken cancellationToken);

    Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
        Guid marketRateId,
        CancellationToken cancellationToken);

    Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
        CompanyId companyId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        string providerKey,
        int lookbackDays,
        string rateType,
        string quoteBasis,
        string rateUseCase,
        CancellationToken cancellationToken);

    Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
        string providerKey,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        int lookbackDays,
        string rateType,
        string quoteBasis,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
        IReadOnlyList<FxMarketRateRecord> marketRates,
        CancellationToken cancellationToken);

    Task<FxSnapshotRecord> UpsertCompanySnapshotAsync(
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
        CancellationToken cancellationToken);

    Task<FxSnapshotRecord> CreateManualCompanySnapshotAsync(
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
        CancellationToken cancellationToken);
}
