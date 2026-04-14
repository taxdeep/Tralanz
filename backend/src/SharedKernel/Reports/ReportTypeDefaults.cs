namespace SharedKernel.Reports;

public static class ReportTypeDefaults
{
    private static readonly IReadOnlyDictionary<ReportType, ReportTypeOption> OptionMap =
        new Dictionary<ReportType, ReportTypeOption>
        {
            [ReportType.Accrual] = new ReportTypeOption
            {
                Type = ReportType.Accrual,
                Code = "accrual",
                Label = "Accrual (Paid & Unpaid)",
                Description = "Recognize income when earned and expenses when incurred, whether or not cash moved.",
                IsRecommended = true
            },
            [ReportType.CashBasis] = new ReportTypeOption
            {
                Type = ReportType.CashBasis,
                Code = "cash_basis",
                Label = "Cash Basis (Paid)",
                Description = "Show only amounts that have been actually received or paid for cash-oriented reporting.",
                IsRecommended = false
            },
            [ReportType.CashOnly] = new ReportTypeOption
            {
                Type = ReportType.CashOnly,
                Code = "cash_only",
                Label = "Cash Only",
                Description = "Restrict the view to direct cash-account movement for the most conservative cash reporting mode.",
                IsRecommended = false
            }
        };

    public static ReportType Default => ReportType.Accrual;

    public static IReadOnlyList<ReportTypeOption> Options { get; } =
    [
        OptionMap[ReportType.Accrual],
        OptionMap[ReportType.CashBasis],
        OptionMap[ReportType.CashOnly]
    ];

    public static ReportTypeOption GetOption(ReportType type) => OptionMap[type];

    public static string ToCode(ReportType type) => GetOption(type).Code;

    public static bool TryParse(string? code, out ReportType type)
    {
        type = Default;

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalized = code.Trim().ToLowerInvariant();

        foreach (var option in Options)
        {
            if (option.Code == normalized)
            {
                type = option.Type;
                return true;
            }
        }

        return false;
    }
}
