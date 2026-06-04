using Citus.Accounting.Application.Queries;
using Xunit;

namespace Citus.Accounting.Application.Tests;

/// <summary>
/// The proportional cash-basis recognition math: full / partial payments,
/// multi-account invoices, repeated applications, direct (fraction-1) entries,
/// and the zero-denominator guard.
/// </summary>
public sealed class CashBasisIncomeMathTests
{
    private static readonly Guid AcctA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AcctB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void FullPayment_RecognisesWholeRevenue()
    {
        // 100 revenue on a 112 (tax-incl) invoice, fully paid (applied 112/112).
        var result = CashBasisIncomeMath.Aggregate(new[]
        {
            new CashBasisContributionRow(AcctA, 112m, 112m, 0m, 100m),
        });

        Assert.Equal(100m, result[AcctA].Credit);
        Assert.Equal(0m, result[AcctA].Debit);
    }

    [Fact]
    public void PartialPayment_RecognisesProportionalRevenue()
    {
        // Half of the 112 invoice paid -> half of the 100 revenue.
        var result = CashBasisIncomeMath.Aggregate(new[]
        {
            new CashBasisContributionRow(AcctA, 56m, 112m, 0m, 100m),
        });

        Assert.Equal(50m, result[AcctA].Credit);
    }

    [Fact]
    public void MultiAccountInvoice_ScalesEachAccountIndependently()
    {
        // Two revenue lines (60 + 40) on a 112 invoice, half paid.
        var result = CashBasisIncomeMath.Aggregate(new[]
        {
            new CashBasisContributionRow(AcctA, 56m, 112m, 0m, 60m),
            new CashBasisContributionRow(AcctB, 56m, 112m, 0m, 40m),
        });

        Assert.Equal(30m, result[AcctA].Credit);
        Assert.Equal(20m, result[AcctB].Credit);
    }

    [Fact]
    public void MultipleApplications_SumToFullRecognition()
    {
        // Two 56 payments on the 112 invoice -> the full 100 revenue.
        var result = CashBasisIncomeMath.Aggregate(new[]
        {
            new CashBasisContributionRow(AcctA, 56m, 112m, 0m, 100m),
            new CashBasisContributionRow(AcctA, 56m, 112m, 0m, 100m),
        });

        Assert.Equal(100m, result[AcctA].Credit);
    }

    [Fact]
    public void DirectEntry_FractionOne_PassesThrough()
    {
        // Direct (non-invoice/bill) expense: debit 75, fraction 1.
        var result = CashBasisIncomeMath.Aggregate(new[]
        {
            new CashBasisContributionRow(AcctA, 1m, 1m, 75m, 0m),
        });

        Assert.Equal(75m, result[AcctA].Debit);
    }

    [Fact]
    public void ZeroDenominator_YieldsNoRecognition()
    {
        var result = CashBasisIncomeMath.Aggregate(new[]
        {
            new CashBasisContributionRow(AcctA, 50m, 0m, 0m, 100m),
        });

        Assert.Equal(0m, result[AcctA].Credit);
    }
}
