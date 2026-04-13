using Citus.Accounting.Infrastructure;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class FxRevaluationCascadePlannerTests
{
    [Fact]
    public void BuildPlan_WhenRequestedBatchIsTail_PreparesRequestedBatchNext()
    {
        var requestedBatchId = Guid.NewGuid();
        var plan = FxRevaluationCascadePlanner.BuildPlan(
            requestedBatchId,
            "FXRV-0001",
            [
                new FxRevaluationCascadePlanner.ActiveRevaluationBatch(
                    requestedBatchId,
                    "FXRV-0001",
                    new DateOnly(2026, 4, 30),
                    new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero))
            ]);

        Assert.True(plan.RequestedBatchIsTail);
        Assert.Equal(requestedBatchId, plan.NextDocumentId);
        Assert.Single(plan.ActiveRevaluationChain);
        Assert.True(plan.ActiveRevaluationChain[0].IsRequestedBatch);
        Assert.True(plan.ActiveRevaluationChain[0].IsNextStep);
    }

    [Fact]
    public void BuildPlan_WhenDescendantExists_SelectsLatestActiveDescendantAsNext()
    {
        var requestedBatchId = Guid.NewGuid();
        var descendantBatchId = Guid.NewGuid();
        var plan = FxRevaluationCascadePlanner.BuildPlan(
            requestedBatchId,
            "FXRV-0001",
            [
                new FxRevaluationCascadePlanner.ActiveRevaluationBatch(
                    descendantBatchId,
                    "FXRV-0002",
                    new DateOnly(2026, 5, 31),
                    new DateTimeOffset(2026, 5, 31, 10, 0, 0, TimeSpan.Zero)),
                new FxRevaluationCascadePlanner.ActiveRevaluationBatch(
                    requestedBatchId,
                    "FXRV-0001",
                    new DateOnly(2026, 4, 30),
                    new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero))
            ]);

        Assert.False(plan.RequestedBatchIsTail);
        Assert.Equal(descendantBatchId, plan.NextDocumentId);
        Assert.Equal("FXRV-0002", plan.NextDisplayNumber);
        Assert.Equal(2, plan.ActiveRevaluationChain.Count);
        Assert.True(plan.ActiveRevaluationChain[0].IsNextStep);
        Assert.False(plan.ActiveRevaluationChain[1].IsNextStep);
    }

    [Fact]
    public void BuildPlan_WhenRequestedBatchIsAlreadyInactive_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FxRevaluationCascadePlanner.BuildPlan(
                Guid.NewGuid(),
                "FXRV-0001",
                [
                    new FxRevaluationCascadePlanner.ActiveRevaluationBatch(
                        Guid.NewGuid(),
                        "FXRV-0002",
                        new DateOnly(2026, 5, 31),
                        new DateTimeOffset(2026, 5, 31, 10, 0, 0, TimeSpan.Zero))
                ]));

        Assert.Contains("FXRV-0001", ex.Message);
        Assert.Contains("no longer active", ex.Message);
    }
}
