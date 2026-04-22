using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Api.Tests;

public sealed class ReceiptGrIrApSettlementStatusPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, ReceiptGrIrApSettlementStatusPolicy.NotEligible)]
    [InlineData(1, 1, 0, 0, 0, ReceiptGrIrApSettlementStatusPolicy.EligibleNotSettled)]
    [InlineData(1, 0, 1, 0, 0, ReceiptGrIrApSettlementStatusPolicy.Blocked)]
    [InlineData(2, 0, 0, 0, 2, ReceiptGrIrApSettlementStatusPolicy.Settled)]
    [InlineData(2, 0, 0, 1, 0, ReceiptGrIrApSettlementStatusPolicy.PartiallySettled)]
    [InlineData(2, 1, 0, 0, 1, ReceiptGrIrApSettlementStatusPolicy.PartiallySettled)]
    public void ResolveSummaryStatus_ReturnsAuthoritativeSettlementStatus(
        int settlementLineCount,
        int eligibleLineCount,
        int blockedLineCount,
        int partiallySettledLineCount,
        int settledLineCount,
        string expected)
    {
        var status = ReceiptGrIrApSettlementStatusPolicy.ResolveSummaryStatus(
            settlementLineCount,
            eligibleLineCount,
            blockedLineCount,
            partiallySettledLineCount,
            settledLineCount);

        Assert.Equal(expected, status);
    }
}
