using SharedKernel.Reports;
using AccountingReportType = SharedKernel.Reports.ReportType;

namespace Modules.Reports.ReportType;

public sealed record class ReportTypeSelection
{
    public string ReportCode { get; init; } = string.Empty;

    public AccountingReportType RequestedType { get; init; }

    public AccountingReportType SelectedType { get; init; }

    public bool WasAdjusted { get; init; }

    public IReadOnlyList<ReportTypeOption> AllowedOptions { get; init; } = Array.Empty<ReportTypeOption>();
}
