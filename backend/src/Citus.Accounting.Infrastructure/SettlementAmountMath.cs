namespace Citus.Accounting.Infrastructure;

internal static class SettlementAmountMath
{
    public static decimal RoundBase(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    public static decimal RoundBase(decimal value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static decimal CalculateCarryingAmountBase(
        decimal appliedAmountTx,
        decimal openAmountTx,
        decimal openAmountBase) =>
        CalculateCarryingAmountBase(appliedAmountTx, openAmountTx, openAmountBase, 2);

    public static decimal CalculateCarryingAmountBase(
        decimal appliedAmountTx,
        decimal openAmountTx,
        decimal openAmountBase,
        int decimals)
    {
        if (appliedAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(appliedAmountTx), "Applied amount must be greater than zero.");
        }

        if (openAmountTx <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openAmountTx), "Open amount must be greater than zero.");
        }

        if (openAmountBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openAmountBase), "Open base amount must be greater than zero.");
        }

        if (appliedAmountTx > openAmountTx)
        {
            throw new InvalidOperationException("Applied amount cannot exceed the remaining open amount.");
        }

        if (appliedAmountTx == openAmountTx)
        {
            return RoundBase(openAmountBase, decimals);
        }

        return RoundBase(openAmountBase * (appliedAmountTx / openAmountTx), decimals);
    }

    public static decimal[] AllocateSettlementBaseAmounts(
        IReadOnlyList<decimal> appliedAmountsTx,
        decimal documentTotalAmountTx,
        decimal fxRate) =>
        AllocateSettlementBaseAmounts(appliedAmountsTx, documentTotalAmountTx, fxRate, 2);

    public static decimal[] AllocateSettlementBaseAmounts(
        IReadOnlyList<decimal> appliedAmountsTx,
        decimal documentTotalAmountTx,
        decimal fxRate,
        int decimals)
    {
        ArgumentNullException.ThrowIfNull(appliedAmountsTx);

        if (appliedAmountsTx.Count == 0)
        {
            return Array.Empty<decimal>();
        }

        var allocated = appliedAmountsTx
            .Select(amount => RoundBase(amount * fxRate, decimals))
            .ToArray();

        var targetTotal = RoundBase(documentTotalAmountTx * fxRate, decimals);
        var delta = RoundBase(targetTotal - allocated.Sum(), decimals);
        if (delta != 0m)
        {
            allocated[^1] = RoundBase(allocated[^1] + delta, decimals);
            if (allocated[^1] <= 0m)
            {
                throw new InvalidOperationException(
                    "Settlement base allocation produced a non-positive final line amount.");
            }
        }

        return allocated;
    }
}
