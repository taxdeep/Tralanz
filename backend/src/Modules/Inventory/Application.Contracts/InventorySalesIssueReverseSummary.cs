namespace Citus.Modules.Inventory.Application.Contracts;

/// <summary>
/// P0-2 (C2): result of running the subledger reverse for a single
/// sales-issue as part of an invoice-reverse. The caller wires this into
/// the audit payload on
/// <c>PostgresAccountingDocumentReviewRepository.CompleteReverseRequestExecutionAsync</c>.
///
/// When <see cref="AlreadyReversed"/> is true the store short-circuited
/// because <c>inventory_documents.reversed_at</c> was non-null; no further
/// state mutation happened. The counts on the result reflect the prior
/// reverse run rather than this re-attempt.
/// </summary>
public sealed record InventorySalesIssueReverseSummary(
    Guid SalesIssueDocumentId,
    Guid InvoiceId,
    bool AlreadyReversed,
    int LineCount,
    decimal TotalQuantityRestored,
    decimal TotalCostBaseRestored,
    DateTimeOffset ReversedAt);
