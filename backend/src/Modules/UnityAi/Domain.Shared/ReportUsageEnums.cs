namespace Citus.Modules.UnityAi.Domain.Shared;

public static class ReportUsageEventType
{
    public const string ReportOpened = "report_opened";
    public const string ReportFiltered = "report_filtered";
    public const string ReportExported = "report_exported";
    public const string ReportPrinted = "report_printed";
    public const string ReportDrilldownClicked = "report_drilldown_clicked";
    public const string ReportAddedToDashboard = "report_added_to_dashboard";
    public const string ReportRemovedFromDashboard = "report_removed_from_dashboard";
    public const string ReportSuggestionAccepted = "report_suggestion_accepted";
    public const string ReportSuggestionDismissed = "report_suggestion_dismissed";
}
