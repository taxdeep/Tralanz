using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Reverses a posted invoice by posting a compensating journal entry
/// (source_type='invoice_reversal') that flips every leg of the original
/// invoice post — AR, revenue, and each per-rule sales-tax leg.
/// </summary>
public sealed record PostInvoiceReverseCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid InvoiceId,
    string? IdempotencyKey = null);

public sealed record PostInvoiceReverseCommandResult(
    Guid InvoiceId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyReversed);
