namespace SharedKernel.FX;

public sealed record class FxRateResolution(
    decimal Rate,
    DateOnly RequestedDate,
    DateOnly EffectiveDate,
    string SourceSemantics,
    string StatusLabel,
    string RateType,
    string QuoteBasis,
    string RateUseCase,
    string PostingReason,
    string? ProviderKey,
    Guid? SnapshotId)
{
    public static FxRateResolution Identity(DateOnly requestedDate) =>
        new(
            1m,
            requestedDate,
            requestedDate,
            FxSourceSemantics.Identity,
            "Base currency",
            FxRateType.Spot,
            FxQuoteBasis.Direct,
            FxRateUseCase.General,
            FxPostingReason.Normal,
            null,
            null);
}
