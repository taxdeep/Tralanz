namespace SharedKernel.FX;

public sealed record class FxRateResolution(
    decimal Rate,
    DateOnly RequestedDate,
    DateOnly EffectiveDate,
    string SourceSemantics,
    string StatusLabel,
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
            null,
            null);
}
