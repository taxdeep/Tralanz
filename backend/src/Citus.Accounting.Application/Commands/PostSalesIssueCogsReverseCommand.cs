using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// P0-2 (C2): triggers the compensating Dr Inventory / Cr COGS journal
/// entry for a previously-posted sales-issue, as part of an invoice-reverse
/// run. The sales-issue document itself is identified by
/// <see cref="SalesIssueDocumentId"/>; the issuing
/// <see cref="ReversedByInvoiceId"/> is recorded in the audit memo for
/// post-incident traceability.
/// </summary>
public sealed record PostSalesIssueCogsReverseCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid SalesIssueDocumentId,
    Guid ReversedByInvoiceId,
    string? IdempotencyKey = null);

/// <summary>
/// Either <see cref="JournalEntryId"/> is set (compensating JE was posted
/// or already existed) or <see cref="ForwardNotPosted"/> is true (rare —
/// the forward COGS JE was never posted because
/// PostInvoiceCommandHandler.TryAutoPostCogsAsync soft-failed). In that
/// case the inventory subledger reverse still runs but no GL compensation
/// is needed.
/// </summary>
public sealed record PostSalesIssueCogsReverseCommandResult(
    Guid SalesIssueDocumentId,
    Guid? JournalEntryId,
    string? JournalEntryDisplayNumber,
    bool AlreadyReversed,
    bool ForwardNotPosted,
    decimal TotalAmountBase);
