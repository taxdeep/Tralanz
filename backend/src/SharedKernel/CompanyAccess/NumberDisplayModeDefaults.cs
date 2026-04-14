namespace SharedKernel.CompanyAccess;

public static class NumberDisplayModeDefaults
{
    public static NumberDisplayMode Default => NumberDisplayMode.CommaDot;

    public static IReadOnlyList<NumberDisplayModeOption> Options { get; } =
        new List<NumberDisplayModeOption>
        {
            new(
                NumberDisplayMode.CommaDot,
                "comma-dot",
                "1,000.00",
                "1,234,567.89",
                "North America and international English default"),
            new(
                NumberDisplayMode.DotComma,
                "dot-comma",
                "1.000,00",
                "1.234.567,89",
                "Common across continental Europe"),
            new(
                NumberDisplayMode.SpaceComma,
                "space-comma",
                "1 000,00",
                "1 234 567,89",
                "French and ISO-style finance-friendly spacing"),
            new(
                NumberDisplayMode.ApostropheDot,
                "apostrophe-dot",
                "1'000.00",
                "1'234'567.89",
                "Swiss-style grouping")
        };

    public static NumberDisplayModeOption GetOption(NumberDisplayMode mode) =>
        Options.FirstOrDefault(option => option.Mode == mode)
        ?? Options[0];

    public static bool TryParseCode(string? code, out NumberDisplayMode mode)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var normalized = code.Trim().ToLowerInvariant();
            foreach (var option in Options)
            {
                if (string.Equals(option.Code, normalized, StringComparison.Ordinal))
                {
                    mode = option.Mode;
                    return true;
                }
            }
        }

        mode = Default;
        return false;
    }

    public static string ToCode(NumberDisplayMode mode) => GetOption(mode).Code;
}
