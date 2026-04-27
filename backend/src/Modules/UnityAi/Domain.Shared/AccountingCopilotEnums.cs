namespace Citus.Modules.UnityAi.Domain.Shared;

/// <summary>
/// Reserved intents the future Accounting Copilot may parse. V1 only ships
/// the no-op planner; no implementation maps these to backend writes.
/// </summary>
public static class AccountingCommandIntent
{
    public const string CreateExpense = "create_expense";
    public const string CreateInvoiceDraft = "create_invoice_draft";
    public const string CreateBillDraft = "create_bill_draft";
    public const string ExplainTransaction = "explain_transaction";
    public const string SearchTransaction = "search_transaction";
    public const string SummarizeMonth = "summarize_month";
    public const string ReconcileBankItem = "reconcile_bank_item";
}
