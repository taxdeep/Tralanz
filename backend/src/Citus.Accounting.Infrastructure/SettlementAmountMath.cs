namespace Citus.Accounting.Infrastructure;

internal static class SettlementAmountMath
{
    public static decimal RoundBase(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    public static decimal CalculateCarryingAmountBase(
        decimal appliedAmountTx,
        decimal openAmountTx,
        decimal openAmountBase)
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
            return RoundBase(openAmountBase);
        }

        return RoundBase(openAmountBase * (appliedAmountTx / openAmountTx));
    }

    public static decimal[] AllocateSettlementBaseAmounts(
        IReadOnlyList<decimal> appliedAmountsTx,
        decimal documentTotalAmountTx,
        decimal fxRate)
    {
        ArgumentNullException.ThrowIfNull(appliedAmountsTx);

        if (appliedAmountsTx.Count == 0)
        {
            return Array.Empty<decimal>();
        }

        var allocated = appliedAmountsTx
            .Select(amount => RoundBase(amount * fxRate))
            .ToArray();

        var targetTotal = RoundBase(documentTotalAmountTx * fxRate);
        var delta = RoundBase(targetTotal - allocated.Sum());
        if (delta != 0m)
        {
            allocated[^1] = RoundBase(allocated[^1] + delta);
            if (allocated[^1] <= 0m)
            {
                throw new InvalidOperationException(
                    "Settlement base allocation produced a non-positive final line amount.");
            }
        }

        return allocated;
    }
}
