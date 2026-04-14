using SharedKernel.Company;

namespace Modules.AP.ControlAccount;

public static class ApControlAccountTemplate
{
    public static ControlAccountProvisioningRequest CreateForeignCurrency(string currencyCode)
    {
        var normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);

        return new ControlAccountProvisioningRequest(
            $"AP-{normalizedCurrencyCode}",
            $"Accounts Payable (A/P) - {normalizedCurrencyCode}",
            "liability",
            "accounts_payable",
            normalizedCurrencyCode,
            $"control_account:accounts_payable:{normalizedCurrencyCode}",
            $"accounts_payable:{normalizedCurrencyCode}",
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
