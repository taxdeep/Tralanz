namespace Citus.Modules.Inventory.Application.Contracts;

public static class ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy
{
    public const string NoEmission = "no_emission";
    public const string Reconciled = "reconciled";
    public const string CostLayerMissing = "cost_layer_missing";
    public const string OrphanCostLayer = "orphan_cost_layer";
    public const string QuantityMismatch = "quantity_mismatch";
    public const string AmountMismatch = "amount_mismatch";

    public static string Resolve(
        int emissionLineCount,
        int costLayerCount,
        int missingCostLayerCount,
        int orphanCostLayerCount,
        decimal emittedQuantity,
        decimal costLayerQuantity,
        decimal emittedCostBase,
        decimal costLayerOriginalCostBase)
    {
        if (emissionLineCount <= 0 && costLayerCount <= 0)
        {
            return NoEmission;
        }

        if (missingCostLayerCount > 0)
        {
            return CostLayerMissing;
        }

        if (orphanCostLayerCount > 0)
        {
            return OrphanCostLayer;
        }

        if (Round6(emittedQuantity) != Round6(costLayerQuantity))
        {
            return QuantityMismatch;
        }

        if (Round6(emittedCostBase) != Round6(costLayerOriginalCostBase))
        {
            return AmountMismatch;
        }

        return Reconciled;
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
