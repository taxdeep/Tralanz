using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// H1: posts the compensating Dr Payment / Cr Expense JE for an
/// Expense Void via the Posting Engine, replacing the legacy
/// hand-rolled SQL in PostgreSqlExpenseStore. Idempotent at the
/// journal layer (source_type='expense_void' + source_id).
/// </summary>
public sealed record PostExpenseVoidCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid ExpenseId,
    string? IdempotencyKey = null);

public sealed record PostExpenseVoidCommandResult(
    Guid ExpenseId,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyVoided);
