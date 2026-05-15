using Modules.AP;

namespace Tests.AP;

public sealed class FxRatePostingPolicyTests
{
    [Fact]
    public void ResolveTransactionToBaseRate_ReturnsIdentityForBaseCurrency()
    {
        var rate = FxRatePostingPolicy.ResolveTransactionToBaseRate(
            inputRate: null,
            transactionCurrencyCode: "CAD",
            baseCurrencyCode: "CAD",
            documentLabel: "expense");

        Assert.Equal(1m, rate);
    }

    [Fact]
    public void ResolveTransactionToBaseRate_RequiresRateForForeignCurrency()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FxRatePostingPolicy.ResolveTransactionToBaseRate(
                inputRate: null,
                transactionCurrencyCode: "USD",
                baseCurrencyCode: "CAD",
                documentLabel: "expense"));

        Assert.Contains("Exchange rate is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTransactionToBaseRate_UsesTransactionToBaseDirection()
    {
        var rate = FxRatePostingPolicy.ResolveTransactionToBaseRate(
            inputRate: 1.3m,
            transactionCurrencyCode: "USD",
            baseCurrencyCode: "CAD",
            documentLabel: "expense");

        Assert.Equal(1.3m, rate);
        Assert.Equal(1.30m, decimal.Round(1m * rate, 2));
    }
}
