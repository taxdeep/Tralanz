using Citus.Accounting.Application.Reconciliation;

namespace Citus.Accounting.Application.Tests;

public sealed class BankReconciliationPolicyTests
{
    [Fact]
    public void Calculate_UsesSignedLedgerAmountsAndAllowsZeroDifference()
    {
        var entries = new[]
        {
            Entry(signedAmountBase: 250m),
            Entry(signedAmountBase: -75m),
        };

        var calculation = BankReconciliationPolicy.Calculate(1_000m, 1_175m, entries);

        Assert.Equal(250m, calculation.ClearedIncrease);
        Assert.Equal(75m, calculation.ClearedDecrease);
        Assert.Equal(1_175m, calculation.CalculatedEndingBalance);
        Assert.True(BankReconciliationPolicy.IsZeroDifference(calculation.Difference));
    }

    [Fact]
    public void Calculate_SupportsCreditCardLiabilityBalanceDirection()
    {
        var entries = new[]
        {
            Entry(signedAmountBase: 120m),
            Entry(signedAmountBase: -40m),
        };

        var calculation = BankReconciliationPolicy.Calculate(500m, 580m, entries);

        Assert.Equal(120m, calculation.ClearedIncrease);
        Assert.Equal(40m, calculation.ClearedDecrease);
        Assert.Equal(580m, calculation.CalculatedEndingBalance);
        Assert.True(BankReconciliationPolicy.IsZeroDifference(calculation.Difference));
    }

    [Fact]
    public void Calculate_FlagsNonZeroDifference()
    {
        var calculation = BankReconciliationPolicy.Calculate(
            openingBalance: 1_000m,
            statementEndingBalance: 1_174.99m,
            entries: new[] { Entry(signedAmountBase: 175m) });

        Assert.Equal(-0.01m, calculation.Difference);
        Assert.False(BankReconciliationPolicy.IsZeroDifference(calculation.Difference));
    }

    [Theory]
    [InlineData(BankReconciliationStatus.InProgress, BankReconciliationStatusTokens.InProgress)]
    [InlineData(BankReconciliationStatus.Completed, BankReconciliationStatusTokens.Completed)]
    [InlineData(BankReconciliationStatus.Abandoned, BankReconciliationStatusTokens.Abandoned)]
    public void StatusTokens_RoundTrip(BankReconciliationStatus status, string token)
    {
        Assert.Equal(token, status.ToToken());
        Assert.Equal(status, BankReconciliationStatusTokens.FromToken(token));
    }

    [Fact]
    public void StatusTokens_RejectsUnknownToken()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BankReconciliationStatusTokens.FromToken("scheduled"));
    }

    private static BankReconciliationLedgerEntry Entry(decimal signedAmountBase) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        new DateOnly(2026, 5, 12),
        Guid.NewGuid(),
        "10100",
        "Operating Bank",
        "JE-0001",
        "manual_journal",
        Guid.NewGuid(),
        "CAD",
        signedAmountBase > 0m ? signedAmountBase : 0m,
        signedAmountBase < 0m ? Math.Abs(signedAmountBase) : 0m,
        signedAmountBase > 0m ? signedAmountBase : 0m,
        signedAmountBase < 0m ? Math.Abs(signedAmountBase) : 0m,
        signedAmountBase,
        signedAmountBase,
        "test");
}
