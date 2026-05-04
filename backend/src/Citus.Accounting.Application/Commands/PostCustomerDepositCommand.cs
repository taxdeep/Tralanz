using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Commands;

/// <summary>
/// M5 iter 3: standalone customer-deposit post. Operator hits Receive
/// Deposit on an SO detail page → this command persists the
/// customer_deposits row, the matching ar_open_items credit row, and
/// posts the journal entry (Dr Bank / Cr Customer Deposit).
/// </summary>
public sealed record PostCustomerDepositCommand(
    CompanyId CompanyId,
    UserId UserId,
    Guid SalesOrderId,
    Guid CustomerId,
    Guid DepositToAccountId,
    decimal AmountTx,
    DateOnly DocumentDate,
    string? Memo = null,
    string? IdempotencyKey = null);

public sealed record PostCustomerDepositCommandResult(
    Guid CustomerDepositId,
    string DisplayNumber,
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    decimal AmountBase);
