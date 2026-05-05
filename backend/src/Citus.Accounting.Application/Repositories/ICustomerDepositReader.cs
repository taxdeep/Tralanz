using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M5 wrap-up: read-side projection for customer deposits scoped to a
/// Sales Order. Surfaces the per-deposit detail + the totals the SO
/// detail page renders ("collected / applied / remaining"). Distinct
/// from <see cref="ICustomerDepositPostingRepository"/> (write side)
/// to keep posting concerns out of the read path.
/// </summary>
public interface ICustomerDepositReader
{
    Task<SalesOrderCustomerDepositSummary> GetForSalesOrderAsync(
        CompanyId companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken);
}

public sealed record SalesOrderCustomerDepositSummary(
    Guid SalesOrderId,
    decimal TotalOriginalBase,
    decimal TotalAppliedBase,
    decimal TotalOpenBase,
    IReadOnlyList<CustomerDepositRow> Deposits);

public sealed record CustomerDepositRow(
    Guid Id,
    string DisplayNumber,
    DateOnly DepositDate,
    decimal OriginalAmountBase,
    decimal AppliedAmountBase,
    decimal OpenAmountBase,
    string Status,
    DateTimeOffset? PostedAt);
