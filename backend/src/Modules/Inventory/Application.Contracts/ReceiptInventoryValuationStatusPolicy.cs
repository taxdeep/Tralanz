namespace Citus.Modules.Inventory.Application.Contracts;

public static class ReceiptInventoryValuationStatusPolicy
{
    public const string NoQuantityActivation = "no_quantity_activation";
    public const string AwaitingBillCoverage = "awaiting_bill_coverage";
    public const string QuantityActivatedUnvalued = "quantity_activated_unvalued";
    public const string PartiallyValued = "partially_valued";
    public const string ValuationBoundaryComplete = "valuation_boundary_complete";
    public const string ValuationInconsistent = "valuation_inconsistent";

    public static string Resolve(
        decimal activatedQuantity,
        decimal billCoveredQuantity,
        decimal valuedQuantity)
    {
        var activated = Round6(activatedQuantity);
        var covered = Round6(billCoveredQuantity);
        var valued = Round6(valuedQuantity);

        if (activated <= 0m)
        {
            return NoQuantityActivation;
        }

        if (covered > activated || valued > activated)
        {
            return ValuationInconsistent;
        }

        if (valued <= 0m && covered <= 0m)
        {
            return AwaitingBillCoverage;
        }

        if (valued <= 0m)
        {
            return QuantityActivatedUnvalued;
        }

        if (valued < activated)
        {
            return PartiallyValued;
        }

        return ValuationBoundaryComplete;
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
