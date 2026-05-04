using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M5 iter 4: prepares a customer-deposit-to-invoice application.
/// Looks up open customer_deposits linked to the invoice's source SO,
/// computes a pro-rata apply amount per the invoice's share of the SO
/// total, persists settlement_applications + ar_open_items adjustments,
/// updates customer_deposits.status when fully cleared, and returns
/// the posting document the engine journalises (Dr Customer Deposit /
/// Cr AR per applied line).
///
/// Returns null Document when nothing was applied (no open deposits
/// for the SO, invoice has no SalesOrderId, share computes to zero).
/// </summary>
public interface ICustomerDepositApplicationRepository
{
    Task<CustomerDepositApplicationPreparation> PrepareApplicationAsync(
        CompanyId companyId,
        UserId userId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken);
}

public sealed record CustomerDepositApplicationPreparation(
    CustomerDepositApplicationDocument? Document,
    IReadOnlyList<CustomerDepositApplicationOutcome> Applications);

/// <summary>One per deposit slice that was actually applied. Empty list when nothing applied.</summary>
public sealed record CustomerDepositApplicationOutcome(
    Guid CustomerDepositId,
    string CustomerDepositDisplayNumber,
    decimal AppliedAmountBase,
    bool DepositFullyClosed);
