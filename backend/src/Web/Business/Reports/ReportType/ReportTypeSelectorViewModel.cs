using SharedKernel.Reports;
using AccountingReportType = SharedKernel.Reports.ReportType;

namespace Web.Business.Reports.ReportType;

public sealed record class ReportTypeSelectorViewModel
{
    public string ReportCode { get; init; } = string.Empty;

    public AccountingReportType SelectedType { get; init; }

    public string SelectedCode { get; init; } = string.Empty;

    public bool WasAdjusted { get; init; }

    public IReadOnlyList<ReportTypeOption> Options { get; init; } = Array.Empty<ReportTypeOption>();
}
