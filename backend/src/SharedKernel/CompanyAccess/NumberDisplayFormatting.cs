using System.Globalization;

namespace SharedKernel.CompanyAccess;

public static class NumberDisplayFormatting
{
    public static string FormatAmount(decimal value, NumberDisplayMode mode, int decimalPlaces = 2) =>
        Format(value, mode, decimalPlaces);

    public static string FormatRate(decimal value, NumberDisplayMode mode, int decimalPlaces = 6) =>
        Format(value, mode, decimalPlaces);

    private static string Format(decimal value, NumberDisplayMode mode, int decimalPlaces)
    {
        var normalized = value.ToString($"N{decimalPlaces}", CultureInfo.InvariantCulture);
        var isNegative = normalized.StartsWith("-", StringComparison.Ordinal);
        if (isNegative)
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.');
        var integerPart = parts[0];
        var decimalPart = parts.Length > 1 ? parts[1] : string.Empty;

        var (groupSeparator, decimalSeparator) = mode switch
        {
            NumberDisplayMode.DotComma => (".", ","),
            NumberDisplayMode.SpaceComma => (" ", ","),
            NumberDisplayMode.ApostropheDot => ("'", "."),
            _ => (",", ".")
        };

        integerPart = integerPart.Replace(",", groupSeparator, StringComparison.Ordinal);
        var result = decimalPlaces > 0
            ? $"{integerPart}{decimalSeparator}{decimalPart}"
            : integerPart;

        return isNegative ? $"-{result}" : result;
    }
}
