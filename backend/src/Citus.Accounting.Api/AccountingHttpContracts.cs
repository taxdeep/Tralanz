using Citus.Accounting.Api;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using Citus.Accounting.Api.Initialization;
using Citus.Accounting.Api.Tasks;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.CoaTemplates;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Statements;
using Citus.Accounting.Application.Reconciliation;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Companies;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Invoices;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Accounting.Infrastructure.Statements;
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Application.Pricing;
using Citus.Modules.Inventory.Domain.Shared;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Citus.Accounting.Api;

internal sealed record class UnitySearchHttpQuery
{
    public CompanyId CompanyId { get; init; }

    public UserId? UserId { get; init; }

    public string? Context { get; init; }

    public string? Query { get; init; }

    public int? Take { get; init; }
}

internal sealed record class UnitySearchRecentHttpQuery
{
    public CompanyId CompanyId { get; init; }

    public UserId? UserId { get; init; }

    public string? Context { get; init; }

    public int? Take { get; init; }
}

internal sealed record class UnitySearchClickHttpRequest
{
    public CompanyId CompanyId { get; init; }

    public UserId UserId { get; init; }

    public string Context { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public Guid SourceId { get; init; }
}

internal sealed record class ManualJournalSaveAndPostHttpRequest
{
    public DateOnly Date { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string? BaseCurrencyCode { get; init; }
    public decimal? ExchangeRate { get; init; }
    public string? Description { get; init; }
    public string? DisplayNumber { get; init; }
    public IReadOnlyList<ManualJournalSaveAndPostLineHttpRequest> Lines { get; init; } =
        Array.Empty<ManualJournalSaveAndPostLineHttpRequest>();
}

internal sealed record class ManualJournalSaveAndPostLineHttpRequest
{
    public Guid AccountId { get; init; }
    public string? Description { get; init; }
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
}

// V1-pending request bodies. Match the frontend Draft records 1:1.
internal sealed record SalesOrderDepositHttpRequest(
    Guid DepositToAccountId,
    decimal AmountTx,
    DateOnly? DocumentDate,
    string? Memo,
    string? IdempotencyKey);

// M6 iter 4: write-off body for the drop-ship clearing workbench.
// ExpectedNetClearingBase carries the operator's read-from-the-page
// residual so the server can detect concurrent activity (a new bill /
// invoice posting between page load and write-off click).
internal sealed record DropShipClearingWriteOffHttpRequest(
    decimal ExpectedNetClearingBase,
    string? Memo,
    string? IdempotencyKey);

// M7 iter 1: target status for an accounting-period transition. The
// repository validates the value against the allowed forward path
// (open → closing → closed → locked); same-value targets are no-ops.
internal sealed record AccountingPeriodTransitionHttpRequest(string TargetStatus);

internal sealed record class SalesReceiptSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }
    public Guid? AcceptedFxSnapshotId { get; init; }
    public Guid DepositToAccountId { get; init; }
    public string DepositToAccountCode { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = "cash";
    public string ReferenceNo { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string CustomerPoNumber { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<SalesReceiptLineHttpRequest> Lines { get; init; } = Array.Empty<SalesReceiptLineHttpRequest>();
}

internal sealed record class SalesReceiptLineHttpRequest
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
    // H6-2b: optional Task back-link. Same semantics as the invoice
    // line wire shape — TaskId alone falls back to legacy whole-task
    // marking, TaskLineId pins to a specific line for partial billing.
    public Guid? TaskId { get; init; }
    public Guid? TaskLineId { get; init; }
}

internal sealed record class RefundReceiptSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }
    public Guid? AcceptedFxSnapshotId { get; init; }
    public Guid RefundFromAccountId { get; init; }
    public string RefundFromAccountCode { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = "cash";
    public string ReferenceNo { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string CustomerPoNumber { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<RefundReceiptLineHttpRequest> Lines { get; init; } = Array.Empty<RefundReceiptLineHttpRequest>();
}

internal sealed record class RefundReceiptLineHttpRequest
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
    // H6-3 (D8): optional task back-link. Refund receipt is the
    // reverse of Sales Receipt — when set, the post handler releases
    // the linked task_lines via RollbackLinesAsync.
    public Guid? TaskId { get; init; }
    public Guid? TaskLineId { get; init; }
}

internal sealed record class CreditMemoSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid CustomerId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }
    public Guid? AcceptedFxSnapshotId { get; init; }
    public string AppliedToInvoiceNumber { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string CustomerPoNumber { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<CreditMemoLineHttpRequest> Lines { get; init; } = Array.Empty<CreditMemoLineHttpRequest>();
}

internal sealed record class CreditMemoLineHttpRequest
{
    public int LineNumber { get; init; }
    public Guid RevenueAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
    // Optional Task back-link. Propagated by "Credit invoice" pre-fill
    // from the source invoice line's task_id; surfaces on the wire and
    // gets persisted into credit_note_lines.task_id so the post handler
    // can roll the linked tasks back to Completed.
    public Guid? TaskId { get; init; }
    // H6-3: optional pin to a specific task_lines row that this credit
    // releases. When present, the post handler routes through the new
    // line-level RollbackLinesAsync path. Null falls back to the
    // legacy whole-task rollback (matches pre-H6-3 behavior).
    public Guid? TaskLineId { get; init; }
}

internal sealed record class VendorCreditSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid VendorId { get; init; }
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal? FxRate { get; init; }
    public Guid? AcceptedFxSnapshotId { get; init; }
    public string AppliedToBillNumber { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<VendorCreditLineHttpRequest> Lines { get; init; } = Array.Empty<VendorCreditLineHttpRequest>();
}

internal sealed record class VendorCreditLineHttpRequest
{
    public int LineNumber { get; init; }
    public Guid ExpenseAccountId { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal LineAmount { get; init; }
    public Guid? TaxCodeId { get; init; }
    public decimal TaxAmount { get; init; }
}

internal sealed record class BankTransferSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid FromAccountId { get; init; }
    public string FromAccountCode { get; init; } = string.Empty;
    public string FromCurrencyCode { get; init; } = string.Empty;
    public Guid ToAccountId { get; init; }
    public string ToAccountCode { get; init; } = string.Empty;
    public string ToCurrencyCode { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public decimal? FxRate { get; init; }
    public Guid? AcceptedFxSnapshotId { get; init; }
    public string ReferenceNo { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
}

internal sealed record class BankDepositSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly DocumentDate { get; init; }
    public Guid DepositToAccountId { get; init; }
    public string DepositToAccountCode { get; init; } = string.Empty;
    public string TransactionCurrencyCode { get; init; } = string.Empty;
    public string ReferenceNo { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
    public IReadOnlyList<BankDepositItemHttpRequest> Items { get; init; } = Array.Empty<BankDepositItemHttpRequest>();
}

internal sealed record class BankDepositItemHttpRequest
{
    public Guid SourceItemId { get; init; }
    public string SourceItemDisplayNumber { get; init; } = string.Empty;
    public string PayerName { get; init; } = string.Empty;
    public string PaymentMethod { get; init; } = string.Empty;
    public string ReferenceNo { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}

internal sealed record class TaxReturnSaveAndPostHttpRequest
{
    public CompanyId CompanyId { get; init; }
    public UserId UserId { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public string TaxRegime { get; init; } = string.Empty;
    public string FilingFrequency { get; init; } = string.Empty;
    public string BaseCurrencyCode { get; init; } = string.Empty;
    public decimal CollectedAmount { get; init; }
    public decimal InputCreditsAmount { get; init; }
    public decimal AdjustmentsAmount { get; init; }
    public string AdjustmentsNote { get; init; } = string.Empty;
    public string RegulatorReferenceNo { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string? IdempotencyKey { get; init; }
}

internal sealed record class DocumentLineHttpRequest
{
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string TaxCode { get; init; } = string.Empty;
}
