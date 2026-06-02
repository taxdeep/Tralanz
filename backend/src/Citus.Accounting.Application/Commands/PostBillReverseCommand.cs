using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// Reverses a posted bill by posting a compensating journal entry
/// (source_type='bill_reversal') that flips every leg of the original bill
/// post — AP, expense, and each per-rule recoverable-tax (ITC) leg.
/// </summary>
public sealed record PostBillReverseCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid BillId,
    string? IdempotencyKey = null);

public sealed record PostBillReverseCommandResult(
    Guid BillId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyReversed);
