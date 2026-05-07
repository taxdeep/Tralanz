using System.Text.Json.Serialization;

namespace SharedKernel.Identity;

[JsonConverter(typeof(EntityNumberJsonConverter))]
public readonly record struct EntityNumber
{
    public const string Prefix = "EN";
    public const int YearWidth = 4;
    public const int OrdinalWidth = 5;
    public const int TotalWidth = 11; // "EN" + YYYY + 5 base36 chars
    public const long MaxOrdinal = 60_466_175L; // 36^5 - 1

    private EntityNumber(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public int Year => int.Parse(Value.AsSpan(2, YearWidth));

    public long Ordinal => Base36.Decode(Value.AsSpan(2 + YearWidth));

    public static EntityNumber Create(int year, long ordinal)
    {
        if (year < 1000 || year > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "Year must be 4 digits (1000-9999).");
        }
        if (ordinal < 0 || ordinal > MaxOrdinal)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), $"Ordinal must be between 0 and {MaxOrdinal}.");
        }
        return new EntityNumber(Prefix + year.ToString("D4") + Base36.Encode(ordinal, OrdinalWidth));
    }

    public static EntityNumber Parse(string text)
    {
        if (!TryParse(text, out var number))
        {
            throw new FormatException($"Invalid entity number: '{text}'.");
        }
        return number;
    }

    public static bool TryParse(string? text, out EntityNumber number)
    {
        number = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var normalized = text.Trim().ToUpperInvariant();
        if (normalized.Length != TotalWidth)
        {
            return false;
        }
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var yearSpan = normalized.AsSpan(2, YearWidth);
        if (!int.TryParse(yearSpan, out var year) || year < 1000 || year > 9999)
        {
            return false;
        }
        var ordinalSpan = normalized.AsSpan(2 + YearWidth);
        if (!Base36.IsValid(ordinalSpan))
        {
            return false;
        }
        number = new EntityNumber(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
