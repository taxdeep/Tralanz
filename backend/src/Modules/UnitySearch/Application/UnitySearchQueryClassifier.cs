using System.Globalization;

namespace Citus.Modules.UnitySearch.Application;

/// <summary>
/// Classifies a normalized search query into one of a handful of buckets so
/// the SQL ranker can apply per-class scoring (e.g. numeric queries get an
/// amount-tier match path; numeric queries also bias entity-type ordering
/// through the per-user query-class prior table).
///
/// The numeric path tolerates user-friendly formatting — leading "$",
/// thousand separators, and trailing ISO currency codes are stripped before
/// parsing. A query that fails to parse falls back to "text".
/// </summary>
public static class UnitySearchQueryClassifier
{
    public static UnitySearchQueryClassification Classify(string? normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return UnitySearchQueryClassification.Empty;
        }

        var stripped = StripFormatting(normalizedQuery);
        if (decimal.TryParse(stripped, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric))
        {
            // Distinguish "11039" (likely entity number / GL code / id) from
            // "11039.18" (almost always an amount). Both feed the SQL via
            // @numeric_query but the ranker uses the class to choose how
            // aggressively to weight amount-tier matches.
            return stripped.Contains('.', StringComparison.Ordinal)
                ? new UnitySearchQueryClassification(UnitySearchQueryClass.NumericDecimal, numeric)
                : new UnitySearchQueryClassification(UnitySearchQueryClass.NumericInt, numeric);
        }

        // Pure ascii alphanumeric with at least one digit and one non-digit
        // is treated as a code (e.g. "INV-001", "JE2024-7"). Falls back to
        // text for everything else.
        if (LooksLikeCode(normalizedQuery))
        {
            return new UnitySearchQueryClassification(UnitySearchQueryClass.Code, null);
        }

        return new UnitySearchQueryClassification(UnitySearchQueryClass.Text, null);
    }

    private static string StripFormatting(string input)
    {
        Span<char> buffer = stackalloc char[input.Length];
        var written = 0;
        foreach (var ch in input)
        {
            if (ch == ',' || ch == '$' || ch == ' ' || ch == '\t')
            {
                continue;
            }
            buffer[written++] = ch;
        }

        var sliced = buffer[..written];
        // Drop a trailing 3-letter ISO currency suffix ("11039.18usd").
        if (sliced.Length > 3 && IsAsciiLetter(sliced[^1]) && IsAsciiLetter(sliced[^2]) && IsAsciiLetter(sliced[^3]))
        {
            sliced = sliced[..^3];
        }
        return new string(sliced);
    }

    private static bool LooksLikeCode(string value)
    {
        var hasDigit = false;
        var hasNonDigit = false;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                hasDigit = true;
            }
            else if (IsAsciiLetter(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '/')
            {
                hasNonDigit = true;
            }
            else
            {
                return false;
            }
        }
        return hasDigit && hasNonDigit;
    }

    private static bool IsAsciiLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
}

public enum UnitySearchQueryClass
{
    Empty,
    Text,
    Code,
    NumericInt,
    NumericDecimal,
}

public sealed record UnitySearchQueryClassification(UnitySearchQueryClass Class, decimal? NumericValue)
{
    public static readonly UnitySearchQueryClassification Empty = new(UnitySearchQueryClass.Empty, null);

    /// <summary>SQL parameter form ("numeric_decimal" / "text" / etc).</summary>
    public string Tag => Class switch
    {
        UnitySearchQueryClass.NumericDecimal => "numeric_decimal",
        UnitySearchQueryClass.NumericInt => "numeric_int",
        UnitySearchQueryClass.Code => "code",
        UnitySearchQueryClass.Text => "text",
        _ => "empty",
    };
}
