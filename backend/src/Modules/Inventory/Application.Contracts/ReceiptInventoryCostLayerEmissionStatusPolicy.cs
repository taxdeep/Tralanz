namespace Citus.Modules.Inventory.Application.Contracts;

public static class ReceiptInventoryCostLayerEmissionStatusPolicy
{
    public const string NoQuantityActivation = "no_quantity_activation";
    public const string AwaitingBillCoverage = "awaiting_bill_coverage";
    public const string AwaitingPostedBill = "awaiting_posted_bill";
    public const string ValuationBackedNotEmitted = "valuation_backed_not_emitted";
    public const string PartiallyEmitted = "partially_emitted";
    public const string FullyEmitted = "fully_emitted";
    public const string EmissionInconsistent = "emission_inconsistent";

    public static string Resolve(
        decimal activatedQuantity,
        decimal valuationBackedQuantity,
        decimal emissionEligibleQuantity,
        decimal emittedQuantity)
    {
        var activated = Round6(activatedQuantity);
        var valuationBacked = Round6(valuationBackedQuantity);
        var eligible = Round6(emissionEligibleQuantity);
        var emitted = Round6(emittedQuantity);

        if (activated <= 0m)
        {
            return NoQuantityActivation;
        }

        if (valuationBacked > activated || eligible > valuationBacked || emitted > eligible)
        {
            return EmissionInconsistent;
        }

        if (valuationBacked <= 0m)
        {
            return AwaitingBillCoverage;
        }

        if (eligible <= 0m)
        {
            return AwaitingPostedBill;
        }

        if (emitted <= 0m)
        {
            return ValuationBackedNotEmitted;
        }

        if (emitted < eligible)
        {
            return PartiallyEmitted;
        }

        return FullyEmitted;
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
