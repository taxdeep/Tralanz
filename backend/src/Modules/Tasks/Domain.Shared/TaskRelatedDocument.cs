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
