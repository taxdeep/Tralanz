using Citus.Accounting.Infrastructure;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class FxRevaluationUnwindMathTests
{
    [Fact]
    public void ReplayRemainingState_PartialSettlement_PreservesRemainingRevaluedCarrying()
    {
        var remaining = FxRevaluationUnwindMath.ReplayRemainingState(
            originalOpenAmountTx: 100m,
            originalAmountBase: 125m,
            appliedAmountsTx: [40m]);

        Assert.Equal(60m, remaining.OpenAmountTx);
        Assert.Equal(75m, remaining.OpenAmountBase);
    }

    [Fact]
    public void ReplayRemainingState_MultipleSettlements_ReplaysRoundedBaseProgression()
    {
        var remaining = FxRevaluationUnwindMath.ReplayRemainingState(
            originalOpenAmountTx: 3m,
            originalAmountBase: 100.01m,
            appliedAmountsTx: [1m, 1m]);

        Assert.Equal(1m, remaining.OpenAmountTx);
        Assert.Equal(33.33m, remaining.OpenAmountBase);
    }
}
