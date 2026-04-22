using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptInventoryCostLayerEmissionStatusPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, ReceiptInventoryCostLayerEmissionStatusPolicy.NoQuantityActivation)]
    [InlineData(5, 0, 0, 0, ReceiptInventoryCostLayerEmissionStatusPolicy.AwaitingBillCoverage)]
    [InlineData(5, 5, 0, 0, ReceiptInventoryCostLayerEmissionStatusPolicy.AwaitingPostedBill)]
    [InlineData(5, 5, 5, 0, ReceiptInventoryCostLayerEmissionStatusPolicy.ValuationBackedNotEmitted)]
    [InlineData(5, 5, 5, 2, ReceiptInventoryCostLayerEmissionStatusPolicy.PartiallyEmitted)]
    [InlineData(5, 5, 5, 5, ReceiptInventoryCostLayerEmissionStatusPolicy.FullyEmitted)]
    [InlineData(5, 6, 5, 5, ReceiptInventoryCostLayerEmissionStatusPolicy.EmissionInconsistent)]
    [InlineData(5, 5, 6, 5, ReceiptInventoryCostLayerEmissionStatusPolicy.EmissionInconsistent)]
    [InlineData(5, 5, 5, 6, ReceiptInventoryCostLayerEmissionStatusPolicy.EmissionInconsistent)]
    public void Resolve_ReturnsExpectedStatus(
        decimal activatedQuantity,
        decimal valuationBackedQuantity,
        decimal emissionEligibleQuantity,
        decimal emittedQuantity,
        string expected)
    {
        var status = ReceiptInventoryCostLayerEmissionStatusPolicy.Resolve(
            activatedQuantity,
            valuationBackedQuantity,
            emissionEligibleQuantity,
            emittedQuantity);

        Assert.Equal(expected, status);
    }
}
