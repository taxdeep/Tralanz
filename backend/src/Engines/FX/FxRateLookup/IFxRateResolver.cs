using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public interface IFxRateResolver
{
    Task<FxRateResolution> ResolveAsync(
        FxRateLookupRequest request,
        CancellationToken cancellationToken);
}
