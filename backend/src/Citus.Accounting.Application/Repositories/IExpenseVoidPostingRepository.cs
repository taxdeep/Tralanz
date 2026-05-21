using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// H1: reads the original Expense's journal entry and assembles a
/// pre-flipped <see cref="ExpenseVoidPostingDocument"/> the engine
/// can dispatch to <c>BuildExpenseVoidFragments</c>. Idempotent: if a
/// JE with source_type='expense_void' + source_id=expenseId already
/// exists, returns it without rebuilding the document.
/// </summary>
public interface IExpenseVoidPostingRepository
{
    Task<ExpenseVoidPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid expenseId,
        CancellationToken cancellationToken);
}

public sealed record ExpenseVoidPostingPreparation(
    ExpenseVoidPostingDocument? Document,
    Guid? ExistingJournalEntryId,
    string? ExistingJournalEntryDisplayNumber);
