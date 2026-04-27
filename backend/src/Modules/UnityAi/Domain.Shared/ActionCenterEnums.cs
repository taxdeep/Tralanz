namespace Citus.Modules.UnityAi.Domain.Shared;

public static class ActionCenterTaskSourceType
{
    public const string Rule = "rule";
    public const string Learning = "learning";
    public const string Ai = "ai";
}

public static class ActionCenterTaskPriority
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Urgent = "urgent";
}

public static class ActionCenterTaskStatus
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";
    public const string Expired = "expired";
    public const string Blocked = "blocked";
}

public static class ActionCenterTaskEventType
{
    public const string Created = "created";
    public const string Viewed = "viewed";
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";
    public const string Expired = "expired";
    public const string Reopened = "reopened";
    public const string ClickedAction = "clicked_action";
}

/// <summary>
/// Stable known task type identifiers. Providers may use additional
/// identifiers; consumers should treat unknown types as opaque strings.
/// </summary>
public static class ActionCenterTaskType
{
    public const string InvoicesOverdue = "invoices_overdue";
    public const string BillsDueSoon = "bills_due_soon";
    public const string BankUnmatchedTransactions = "bank_unmatched_transactions";
    public const string ReconciliationOverdue = "reconciliation_overdue";
    public const string SystemSetupCompanyProfile = "system_setup.company_profile";
    public const string SystemSetupSmtp = "system_setup.smtp";
    public const string SystemSetupSalesTax = "system_setup.sales_tax";
    public const string SystemSetupInvoiceTemplate = "system_setup.invoice_template";
    public const string SalesTaxFilingDue = "sales_tax_filing_due";
    public const string AiSoftSuggestion = "ai.soft_suggestion";
}
