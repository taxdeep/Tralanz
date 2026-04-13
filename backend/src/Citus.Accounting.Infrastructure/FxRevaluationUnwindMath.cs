namespace Citus.Accounting.Infrastructure;

internal static class FxRevaluationUnwindMath
{
    public static RemainingOpenItemState ReplayRemainingState(
        decimal originalOpenAmountTx,
        decimal originalAmountBase,
        IReadOnlyList<decimal> appliedAmountsTx)
    {
        ArgumentNullException.ThrowIfNull(appliedAmountsTx);

        if (originalOpenAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalOpenAmountTx),
                "Original open amount must be greater than zero.");
        }

        if (originalAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalAmountBase),
                "Original base amount must be greater than zero.");
        }

        var remainingTx = Round6(originalOpenAmountTx);
        var remainingBase = Round6(originalAmountBase);

        foreach (var appliedAmountTx in appliedAmountsTx)
        {
            if (appliedAmountTx <= 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(appliedAmountsTx),
                    "Applied settlement amounts must be greater than zero.");
            }

            if (appliedAmountTx > remainingTx)
            {
                throw new InvalidOperationException(
                    "Settlement replay exceeded the remaining open amount.");
            }

            var consumedBase = appliedAmountTx == remainingTx
                ? Round6(remainingBase)
                : Round6(SettlementAmountMath.CalculateCarryingAmountBase(
                    appliedAmountTx,
                    remainingTx,
                    remainingBase));

            remainingTx = Round6(remainingTx - appliedAmountTx);
            remainingBase = Round6(remainingBase - consumedBase);
        }

        return new RemainingOpenItemState(remainingTx, remainingBase);
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    internal sealed record RemainingOpenItemState(
        decimal OpenAmountTx,
        decimal OpenAmountBase);
}
