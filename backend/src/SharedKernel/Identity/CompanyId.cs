namespace SharedKernel.Identity;

public readonly record struct CompanyId
{
    public const char Prefix = 'C';
    public const int OrdinalWidth = 6;
    public const long MaxOrdinal = 2_176_782_335L; // 36^6 - 1

    private CompanyId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public long Ordinal => Base36.Decode(Value.AsSpan(1));

    public static CompanyId FromOrdinal(long ordinal)
    {
        if (ordinal < 0 || ordinal > MaxOrdinal)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), $"Ordinal must be between 0 and {MaxOrdinal}.");
        }
        return new CompanyId(Prefix + Base36.Encode(ordinal, OrdinalWidth));
    }

    public static CompanyId Parse(string text)
    {
        if (!TryParse(text, out var id))
        {
            throw new FormatException($"Invalid company id: '{text}'.");
        }
        return id;
    }

    public static bool TryParse(string? text, out CompanyId id)
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
        id = new CompanyId(normalized);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
