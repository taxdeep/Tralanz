using SharedKernel.Company;

namespace Modules.AR.ControlAccount;

public static class ArControlAccountTemplate
{
    public static ControlAccountProvisioningRequest CreateForeignCurrency(string currencyCode)
    {
        var normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);

        return new ControlAccountProvisioningRequest(
            $"AR-{normalizedCurrencyCode}",
            $"Accounts Receivable (A/R) - {normalizedCurrencyCode}",
            "asset",
            "accounts_receivable",
            normalizedCurrencyCode,
            $"control_account:accounts_receivable:{normalizedCurrencyCode}",
            $"accounts_receivable:{normalizedCurrencyCode}",
            false);
    }

    private static string NormalizeCurrencyCode(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException("A currency code is required.");
        }

        return currencyCode.Trim().ToUpperInvariant();
    }
}
