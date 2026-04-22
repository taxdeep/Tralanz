using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptInventoryValuationStatusPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, ReceiptInventoryValuationStatusPolicy.NoQuantityActivation)]
    [InlineData(5, 0, 0, ReceiptInventoryValuationStatusPolicy.AwaitingBillCoverage)]
    [InlineData(5, 5, 0, ReceiptInventoryValuationStatusPolicy.QuantityActivatedUnvalued)]
    [InlineData(5, 5, 2, ReceiptInventoryValuationStatusPolicy.PartiallyValued)]
    [InlineData(5, 5, 5, ReceiptInventoryValuationStatusPolicy.ValuationBoundaryComplete)]
    [InlineData(5, 5, 6, ReceiptInventoryValuationStatusPolicy.ValuationInconsistent)]
    [InlineData(5, 6, 5, ReceiptInventoryValuationStatusPolicy.ValuationInconsistent)]
    public void Resolve_ReturnsExpectedStatus(
        decimal activatedQuantity,
        decimal billCoveredQuantity,
        decimal valuedQuantity,
        string expected)
    {
        var status = ReceiptInventoryValuationStatusPolicy.Resolve(
            activatedQuantity,
            billCoveredQuantity,
            valuedQuantity);

        Assert.Equal(expected, status);
    }
}
