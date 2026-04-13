using Citus.Accounting.Infrastructure;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class FxRevaluationChainGuardTests
{
    [Fact]
    public void EnsureNoActiveDescendantRevaluation_AllowsChainTail()
    {
        FxRevaluationChainGuard.EnsureNoActiveDescendantRevaluation(
            "FXRV-0001",
            descendant: null);
    }

    [Fact]
    public void EnsureNoActiveDescendantRevaluation_RejectsActiveDescendant()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FxRevaluationChainGuard.EnsureNoActiveDescendantRevaluation(
                "FXRV-0001",
                new FxRevaluationChainGuard.ActiveDescendantRevaluation(
                    Guid.NewGuid(),
                    "FXRV-0002",
                    "ar_open_item",
                    Guid.Parse("11111111-1111-1111-1111-111111111111"))));

        Assert.Contains("FXRV-0001", ex.Message);
        Assert.Contains("FXRV-0002", ex.Message);
        Assert.Contains("latest active revaluation batch", ex.Message);
    }
}
