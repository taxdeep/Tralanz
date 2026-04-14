using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public interface IFxProviderClient
{
    Task<IReadOnlyList<FxMarketRateRecord>> GetRatesAsync(
        string providerKey,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
