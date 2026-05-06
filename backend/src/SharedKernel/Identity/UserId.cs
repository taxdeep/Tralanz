using System.Text.Json.Serialization;

namespace SharedKernel.Identity;

[JsonConverter(typeof(UserIdJsonConverter))]
public readonly record struct UserId
{
    public const char Prefix = 'U';
    public const int OrdinalWidth = 6;
    public const long MaxOrdinal = 2_176_782_335L; // 36^6 - 1

    private UserId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public long Ordinal => Base36.Decode(Value.AsSpan(1));

    public static UserId FromOrdinal(long ordinal)
    {
        if (ordinal < 0 || ordinal > MaxOrdinal)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), $"Ordinal must be between 0 and {MaxOrdinal}.");
        }
        return new UserId(Prefix + Base36.Encode(ordinal, OrdinalWidth));
    }

    public static UserId Parse(string text)
    {
        if (!TryParse(text, out var id))
        {
            throw new FormatException($"Invalid user id: '{text}'.");
        }
        return id;
    }

    public static bool TryParse(string? text, out UserId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var normalized = text.Trim().ToUpperInvariant();
        if (normalized.Length != 1 + OrdinalWidth)
        {
            return false;
        }
        if (normalized[0] != Prefix)
        {
            return false;
        }
        if (!Base36.IsValid(normalized.AsSpan(1)))
        {
            return false;
        }
        id = new UserId(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
