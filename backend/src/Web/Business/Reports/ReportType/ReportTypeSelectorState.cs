using Modules.Reports.ReportType;
using SharedKernel.Reports;
using AccountingReportType = SharedKernel.Reports.ReportType;

namespace Web.Business.Reports.ReportType;

public static class ReportTypeSelectorState
{
    public static ReportTypeSelectorViewModel Build(
        string reportCode,
        AccountingReportType? requestedType)
    {
        var selection = ReportTypePolicy.Resolve(reportCode, requestedType);

        return new ReportTypeSelectorViewModel
        {
            ReportCode = selection.ReportCode,
            SelectedType = selection.SelectedType,
            SelectedCode = ReportTypeDefaults.ToCode(selection.SelectedType),
            WasAdjusted = selection.WasAdjusted,
            Options = selection.AllowedOptions
        };
    }
}
