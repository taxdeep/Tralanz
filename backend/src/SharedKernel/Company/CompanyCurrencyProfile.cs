namespace SharedKernel.Company;

public sealed record class CompanyCurrencyProfile(
    Guid CompanyId,
    string LegalName,
    string BaseCurrencyCode,
    bool MultiCurrencyEnabled,
    IReadOnlyList<CompanyCurrencyOption> Currencies)
{
    public IReadOnlyList<CompanyCurrencyOption> EnabledCurrencies =>
        Currencies
            .Where(static currency => currency.IsEnabled)
            .OrderByDescending(static currency => currency.IsBaseCurrency)
            .ThenBy(static currency => currency.CurrencyCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool IsCurrencyEnabled(string currencyCode) =>
        EnabledCurrencies.Any(currency =>
            string.Equals(currency.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase));
}
