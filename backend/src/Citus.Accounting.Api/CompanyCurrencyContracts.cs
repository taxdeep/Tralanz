using SharedKernel.Company;

namespace Citus.Accounting.Api;

public sealed record EnableCompanyCurrencyHttpRequest(
    string CurrencyCode);

internal static class CompanyCurrencyResponseMapper
{
    public static object MapCurrencyProfile(CompanyCurrencyProfile profile) => new
    {
        profile.CompanyId,
        profile.LegalName,
        profile.BaseCurrencyCode,
        profile.MultiCurrencyEnabled,
        Currencies = profile.Currencies.Select(static currency => new
        {
            currency.CurrencyCode,
            currency.CurrencyName,
            currency.IsBaseCurrency,
            currency.IsEnabled
        }).ToArray()
    };
}
