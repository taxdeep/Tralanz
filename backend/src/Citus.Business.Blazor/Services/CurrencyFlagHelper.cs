namespace Citus.Business.Blazor.Services;

/// <summary>
/// Renders a unicode flag emoji from an ISO 4217 currency code by
/// looking up the country (or region) the currency belongs to and
/// composing the two regional-indicator code points. Coverage matches
/// frankfurter.dev's published list — a currency we can't recommend a
/// rate for is also a currency we don't show a flag for.
/// </summary>
public static class CurrencyFlagHelper
{
    private static readonly Dictionary<string, string> CurrencyToCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUD"] = "AU",
        ["BGN"] = "BG",
        ["BRL"] = "BR",
        ["CAD"] = "CA",
        ["CHF"] = "CH",
        ["CNY"] = "CN",
        ["CZK"] = "CZ",
        ["DKK"] = "DK",
        ["EUR"] = "EU",
        ["GBP"] = "GB",
        ["HKD"] = "HK",
        ["HUF"] = "HU",
        ["IDR"] = "ID",
        ["ILS"] = "IL",
        ["INR"] = "IN",
        ["ISK"] = "IS",
        ["JPY"] = "JP",
        ["KRW"] = "KR",
        ["MXN"] = "MX",
        ["MYR"] = "MY",
        ["NOK"] = "NO",
        ["NZD"] = "NZ",
        ["PHP"] = "PH",
        ["PLN"] = "PL",
        ["RON"] = "RO",
        ["SEK"] = "SE",
        ["SGD"] = "SG",
        ["THB"] = "TH",
        ["TRY"] = "TR",
        ["USD"] = "US",
        ["ZAR"] = "ZA"
    };

    public static string? GetFlagEmoji(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            return null;
        }
        if (!CurrencyToCountry.TryGetValue(currencyCode.Trim(), out var country))
        {
            return null;
        }
        return CountryToFlag(country);
    }

    private static string CountryToFlag(string countryCode)
    {
        // Regional Indicator Symbols start at U+1F1E6 ('A'). Each ASCII
        // letter maps to the corresponding regional indicator; two of
        // them adjacent render as a flag.
        const int RegionalIndicatorBase = 0x1F1E6;
        const int AsciiABase = 'A';

        Span<int> codePoints = stackalloc int[2];
        codePoints[0] = RegionalIndicatorBase + (char.ToUpperInvariant(countryCode[0]) - AsciiABase);
        codePoints[1] = RegionalIndicatorBase + (char.ToUpperInvariant(countryCode[1]) - AsciiABase);
        return char.ConvertFromUtf32(codePoints[0]) + char.ConvertFromUtf32(codePoints[1]);
    }
}
