namespace Citus.Modules.UnityAi.Domain.Shared;

/// <summary>
/// Stable identifiers for AI-eligible tasks routed through the unityAI Gateway.
/// New types may be added; existing values must not change semantics.
/// </summary>
public static class UnityAiTaskType
{
    public const string UnitysearchLearningSummary = "unitysearch_learning_summary";
    public const string UnitysearchAliasSuggestion = "unitysearch_alias_suggestion";
    public const string UnitysearchRankingHintGeneration = "unitysearch_ranking_hint_generation";

    public const string ReportUsageSummary = "report_usage_summary";
    public const string DashboardWidgetRecommendation = "dashboard_widget_recommendation";
    public const string DashboardSummary = "dashboard_summary";
    public const string TaskPrioritySummary = "task_priority_summary";
    public const string BusinessActionSuggestion = "business_action_suggestion";

    public const string AccountingCommandParse = "accounting_command_parse";
    public const string ReceiptOcrExtract = "receipt_ocr_extract";
    public const string InvoiceFieldExtract = "invoice_field_extract";
    public const string BillFieldExtract = "bill_field_extract";
    public const string BankMemoParse = "bank_memo_parse";

    public const string FinancialInsightSummary = "financial_insight_summary";
    public const string AnomalyExplanation = "anomaly_explanation";
    public const string EmailDraftGeneration = "email_draft_generation";
}
