using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Application.Tests;

public sealed class PurchaseOrderPurchaseVariancePostingReadinessPolicyTests
{
    [Fact]
    public void ResolveStatus_ReturnsNotApplicable_WhenNoVarianceLinesExist()
    {
        var status = PurchaseOrderPurchaseVariancePostingReadinessPolicy.ResolveStatus(
            varianceLineCount: 0,
            candidateLineCount: 0,
            blockedLineCount: 0);

        Assert.Equal(PurchaseOrderPurchaseVariancePostingReadinessPolicy.NotApplicable, status);
        Assert.False(PurchaseOrderPurchaseVariancePostingReadinessPolicy.CanRequestPosting(status));
    }

    [Fact]
    public void ResolveStatus_ReturnsBlocked_WhenAnyLineIsBlocked()
    {
        var status = PurchaseOrderPurchaseVariancePostingReadinessPolicy.ResolveStatus(
            varianceLineCount: 3,
            candidateLineCount: 2,
            blockedLineCount: 1);

        Assert.Equal(PurchaseOrderPurchaseVariancePostingReadinessPolicy.Blocked, status);
        Assert.False(PurchaseOrderPurchaseVariancePostingReadinessPolicy.CanRequestPosting(status));
    }

    [Fact]
    public void ResolveStatus_ReturnsReadyForPosting_WhenCandidatesExistWithoutBlocks()
    {
        var status = PurchaseOrderPurchaseVariancePostingReadinessPolicy.ResolveStatus(
            varianceLineCount: 2,
            candidateLineCount: 2,
            blockedLineCount: 0);

        Assert.Equal(PurchaseOrderPurchaseVariancePostingReadinessPolicy.ReadyForPosting, status);
        Assert.True(PurchaseOrderPurchaseVariancePostingReadinessPolicy.CanRequestPosting(status));
    }

    [Fact]
    public void ResolveStatus_ReturnsNoVariance_WhenLinesExistWithoutCandidatesOrBlocks()
    {
        var status = PurchaseOrderPurchaseVariancePostingReadinessPolicy.ResolveStatus(
            varianceLineCount: 1,
            candidateLineCount: 0,
            blockedLineCount: 0);

        Assert.Equal(PurchaseOrderPurchaseVariancePostingReadinessPolicy.NoVariance, status);
        Assert.False(PurchaseOrderPurchaseVariancePostingReadinessPolicy.CanRequestPosting(status));
    }
}
