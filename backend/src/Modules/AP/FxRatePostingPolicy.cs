namespace Modules.AP;

public static class FxRatePostingPolicy
{
    public static decimal ResolveTransactionToBaseRate(
        decimal? inputRate,
        string transactionCurrencyCode,
        string baseCurrencyCode,
        string documentLabel)
    {
        var transactionCurrency = NormalizeCurrency(transactionCurrencyCode, "Transaction currency");
        var baseCurrency = NormalizeCurrency(baseCurrencyCode, "Base currency");
        var sameCurrency = string.Equals(transactionCurrency, baseCurrency, StringComparison.Ordinal);

        if (sameCurrency)
        {
            if (inputRate is { } sameCurrencyRate && sameCurrencyRate <= 0m)
            {
                throw new InvalidOperationException("Exchange rate must be greater than zero.");
            }

            return 1m;
        }

        if (inputRate is null)
        {
            throw new InvalidOperationException(
                $"Exchange rate is required when {documentLabel} currency differs from company base currency.");
        }

        if (inputRate.Value <= 0m)
        {
            throw new InvalidOperationException("Exchange rate must be greater than zero.");
        }

        return inputRate.Value;
    }

    private static string NormalizeCurrency(string currencyCode, string label)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException($"{label} is required.");
        }

        var normalized = currencyCode.Trim().ToUpperInvariant();
        if (normalized.Length != 3)
        {
            throw new InvalidOperationException($"{label} must be a 3-letter code.");
        }

        return normalized;
    }
}
