using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M5 iter 3: prepares a standalone Customer Deposit. Generates the
/// entity / display numbers, inserts the customer_deposits row, inserts
/// the matching ar_open_items row (source_type='customer_deposit',
/// balance_side='credit'), and returns a posting document the engine
/// can journalise (Dr Bank / Cr Customer Deposit).
///
/// Idempotency probe: if a customer_deposits row already exists for the
/// supplied IdempotencyKey (per company), returns the existing display /
/// JE info via <see cref="ExistingDepositId"/> so the caller can short
/// circuit without double-posting.
/// </summary>
public interface ICustomerDepositPostingRepository
{
    Task<CustomerDepositPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        CustomerDepositPostingRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Operator-supplied input for a standalone deposit. Currency / FX
/// resolution happens server-side (V1: assumes deposit currency equals
/// SO currency; multi-currency FX snapshot is wired in a later iter).
/// </summary>
public sealed record CustomerDepositPostingRequest(
    Guid SalesOrderId,
    Guid CustomerId,
    Guid DepositToAccountId,
    decimal AmountTx,
    DateOnly DocumentDate,
    string? Memo,
    string? IdempotencyKey);

public sealed record CustomerDepositPostingPreparation(
    CustomerDepositPostingDocument? Document,
    Guid? ExistingDepositId,
    string? ExistingDisplayNumber);
