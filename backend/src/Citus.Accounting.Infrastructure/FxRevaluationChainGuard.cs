namespace Citus.Accounting.Infrastructure;

internal static class FxRevaluationChainGuard
{
    public static void EnsureNoActiveDescendantRevaluation(
        string sourceDisplayNumber,
        ActiveDescendantRevaluation? descendant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDisplayNumber);

        if (descendant is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"FX revaluation batch {sourceDisplayNumber} cannot prepare next-period unwind while later revaluation batch {descendant.DisplayNumber} remains active for {descendant.TargetOpenItemType} {descendant.TargetOpenItemId}. Unwind the latest active revaluation batch in the chain first.");
    }

    internal sealed record ActiveDescendantRevaluation(
        Guid BatchId,
        string DisplayNumber,
        string TargetOpenItemType,
        Guid TargetOpenItemId);
}
