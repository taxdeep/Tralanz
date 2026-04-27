namespace Citus.Modules.UnityAi.Domain.Shared;

public static class AiJobRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Partial = "partial";
    public const string Skipped = "skipped";
}

public static class AiJobRunTriggerType
{
    public const string Manual = "manual";
    public const string Scheduled = "scheduled";
    public const string System = "system";
    public const string Test = "test";
}

public static class AiJobType
{
    public const string UnitysearchLearning = "unitysearch_learning";
    public const string ReportUsageLearning = "report_usage_learning";
    public const string DashboardRecommendation = "dashboard_recommendation";
    public const string ActionCenterGeneration = "action_center_generation";
    public const string AiHintValidation = "ai_hint_validation";
    public const string AccountingCommandParse = "accounting_command_parse";
    public const string ReceiptOcr = "receipt_ocr";
}

public static class AiRequestLogStatus
{
    public const string Skipped = "skipped";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string InvalidOutput = "invalid_output";
}
