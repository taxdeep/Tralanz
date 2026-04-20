using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptInventoryCostLayerEmissionReconciliationStatusPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0, ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.NoEmission)]
    [InlineData(1, 1, 0, 0, 5, 5, 25, 25, ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.Reconciled)]
    [InlineData(1, 0, 1, 0, 5, 0, 25, 0, ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.CostLayerMissing)]
    [InlineData(1, 2, 0, 1, 5, 7, 25, 35, ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.OrphanCostLayer)]
    [InlineData(1, 1, 0, 0, 5, 4, 25, 25, ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.QuantityMismatch)]
    [InlineData(1, 1, 0, 0, 5, 5, 25, 24, ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.AmountMismatch)]
    public void Resolve_ReturnsExpectedStatus(
        int emissionLineCount,
        int costLayerCount,
        int missingCostLayerCount,
        int orphanCostLayerCount,
        decimal emittedQuantity,
        decimal costLayerQuantity,
        decimal emittedCostBase,
        decimal costLayerOriginalCostBase,
        string expected)
    {
        var status = ReceiptInventoryCostLayerEmissionReconciliationStatusPolicy.Resolve(
            emissionLineCount,
            costLayerCount,
            missingCostLayerCount,
            orphanCostLayerCount,
            emittedQuantity,
            costLayerQuantity,
            emittedCostBase,
            costLayerOriginalCostBase);

        Assert.Equal(expected, status);
    }
}
