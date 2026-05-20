namespace Citus.Modules.Tasks.Domain.Shared;

/// <summary>
/// One row of the "related documents" rollup shown on TaskDetailPage —
/// a single AR or AP document that has at least one line linked to the
/// task via the *_lines.task_id columns added by Batch 8.
/// <see cref="TaskAmount"/> is the sum of THIS task's lines on the doc
/// (not the doc total — a single invoice can bill multiple tasks; we
/// only show this task's share). <see cref="NavigationHref"/> is the
/// relative URL the operator clicks to open the doc detail page.
/// </summary>
public sealed record class TaskRelatedDocument
{
    public required string DocumentType { get; init; }

    public required Guid DocumentId { get; init; }

    public required string DisplayNumber { get; init; }

    public required DateOnly DocumentDate { get; init; }

    public required string Status { get; init; }

    public required decimal TaskAmount { get; init; }

    public required string CurrencyCode { get; init; }

    public required string NavigationHref { get; init; }

    /// <summary>
    /// Company base currency the *Base fields are denominated in. Same
    /// for every row in a single call — the API resolves it from the
    /// active session's company once and stamps each row so the UI
    /// doesn't need a side query.
    /// </summary>
    public required string BaseCurrencyCode { get; init; }

    /// <summary>
    /// The document's own posted FX rate (1 <see cref="CurrencyCode"/>
    /// = N <see cref="BaseCurrencyCode"/>) — i.e. <c>invoices.fx_rate</c>,
    /// <c>credit_notes.fx_rate</c>, <c>bills.fx_rate</c>, or
    /// <c>expenses.fx_rate</c> depending on the doc type. This is the
    /// rate the GL booked the doc at, so a sum over <c>TaskAmountBase</c>
    /// reconciles against the relevant Revenue / Expense ledger
    /// account balances. Falls back to <c>1</c> when the doc has no
    /// recorded rate (shouldn't happen for posted docs but the SQL
    /// is defensive).
    /// </summary>
    public required decimal FxRate { get; init; }

    /// <summary>
    /// <see cref="TaskAmount"/> × <see cref="FxRate"/>, rounded to 2
    /// decimal places. The base-currency contribution this doc made to
    /// the task's revenue or cost — what the GL actually booked.
    /// </summary>
    public required decimal TaskAmountBase { get; init; }
}

/// <summary>
/// Token constants for the <see cref="TaskRelatedDocument.DocumentType"/>
/// field. Matches the SQL <c>'invoice' / 'credit_note' / 'bill' /
/// 'expense'</c> tokens emitted by the Postgres query.
/// </summary>
public static class TaskRelatedDocumentType
{
    public const string Invoice = "invoice";
    public const string CreditNote = "credit_note";
    public const string Bill = "bill";
    public const string Expense = "expense";
}
