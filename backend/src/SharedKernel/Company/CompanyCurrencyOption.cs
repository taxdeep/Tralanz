namespace SharedKernel.Company;

public sealed record class CompanyCurrencyOption(
    string CurrencyCode,
    string CurrencyName,
    bool IsBaseCurrency,
    bool IsEnabled)
{
    public string DisplayText => $"{CurrencyCode} {CurrencyName}";
}
