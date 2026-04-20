using Citus.Modules.Inventory.Application.Contracts;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptGrIrBridgeStatusPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, ReceiptGrIrBridgeStatusPolicy.NotEligible)]
    [InlineData(1, 1, 0, 0, 0, 0, ReceiptGrIrBridgeStatusPolicy.EligibleNotPosted)]
    [InlineData(2, 1, 1, 0, 0, 0, ReceiptGrIrBridgeStatusPolicy.BlockedReconciliationRequired)]
    [InlineData(2, 1, 0, 1, 0, 0, ReceiptGrIrBridgeStatusPolicy.BlockedVarianceRequired)]
    [InlineData(2, 0, 0, 0, 2, 0, ReceiptGrIrBridgeStatusPolicy.Posted)]
    [InlineData(2, 1, 0, 0, 1, 0, ReceiptGrIrBridgeStatusPolicy.PartiallyPosted)]
    [InlineData(2, 1, 0, 0, 0, 1, ReceiptGrIrBridgeStatusPolicy.PartiallyPosted)]
    public void Resolve_ReturnsExpectedStatus(
        int bridgeLineCount,
        int eligibleLineCount,
        int blockedReconciliationLineCount,
        int blockedVarianceLineCount,
        int postedLineCount,
        int partiallyPostedLineCount,
        string expected)
    {
        var status = ReceiptGrIrBridgeStatusPolicy.Resolve(
            bridgeLineCount,
            eligibleLineCount,
            blockedReconciliationLineCount,
            blockedVarianceLineCount,
            postedLineCount,
            partiallyPostedLineCount);

        Assert.Equal(expected, status);
    }
}
