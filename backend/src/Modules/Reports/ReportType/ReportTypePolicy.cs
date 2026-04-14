using SharedKernel.Reports;
using AccountingReportType = SharedKernel.Reports.ReportType;

namespace Modules.Reports.ReportType;

public static class ReportTypePolicy
{
    private static readonly IReadOnlySet<string> FullAccountingBasisReports =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "trial_balance",
            "income_statement",
            "balance_sheet",
            "ar_aging",
            "ap_aging"
        };

    public static IReadOnlyList<ReportTypeOption> GetAllowedOptions(string reportCode)
    {
        var normalized = NormalizeReportCode(reportCode);

        if (normalized is null)
        {
            return [ReportTypeDefaults.GetOption(ReportTypeDefaults.Default)];
        }

        return FullAccountingBasisReports.Contains(normalized)
            ? ReportTypeDefaults.Options
            : [ReportTypeDefaults.GetOption(ReportTypeDefaults.Default)];
    }

    public static ReportTypeSelection Resolve(string reportCode, AccountingReportType? requestedType)
    {
        var normalized = NormalizeReportCode(reportCode) ?? "unknown";
        var allowedOptions = GetAllowedOptions(normalized);
        var effectiveRequestedType = requestedType ?? ReportTypeDefaults.Default;

        foreach (var option in allowedOptions)
        {
            if (option.Type == effectiveRequestedType)
            {
                return new ReportTypeSelection
                {
                    ReportCode = normalized,
                    RequestedType = effectiveRequestedType,
                    SelectedType = effectiveRequestedType,
                    WasAdjusted = false,
                    AllowedOptions = allowedOptions
                };
            }
        }

        return new ReportTypeSelection
        {
            ReportCode = normalized,
            RequestedType = effectiveRequestedType,
            SelectedType = ReportTypeDefaults.Default,
            WasAdjusted = true,
            AllowedOptions = allowedOptions
        };
    }

    private static string? NormalizeReportCode(string? reportCode)
    {
        if (string.IsNullOrWhiteSpace(reportCode))
        {
            return null;
        }

        return reportCode.Trim().ToLowerInvariant();
    }
}
