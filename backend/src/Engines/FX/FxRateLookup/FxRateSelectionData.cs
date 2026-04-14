using SharedKernel.FX;

namespace Engines.FX.FxRateLookup;

public sealed record class FxRateSelectionData(
    IReadOnlyList<FxSnapshotRecord> CompanySnapshots,
    IReadOnlyList<FxMarketRateRecord> MarketRates);
