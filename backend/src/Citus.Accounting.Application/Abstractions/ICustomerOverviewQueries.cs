namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Read-only aggregations for the Customer detail page's two header
/// surfaces:
///   * Financial summary card (open balance, overdue count, unbilled
///     work) — populates the right-side stat block.
///   * Transactions tab — unified timeline of invoices, sales orders
///     and quotes for one customer with optional filters.
///
/// Both queries are scoped by (companyId, customerId) so cross-tenant
/// reads can't slip through. Open balance and overdue counts come from
/// <c>ar_open_items</c>; unbilled work is hardcoded to 0 today and
/// becomes real once the Task module ships.
/// </summary>
public interface ICustomerOverviewQueries
{
    Task<CustomerFinancialSummary> GetFinancialSummaryAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerTransactionRow>> ListTransactionsAsync(
        CompanyId companyId,
        Guid customerId,
        CustomerTransactionFilter filter,
        CancellationToken cancellationToken);
}

public sealed record CustomerFinancialSummary(
    decimal OpenBalanceBase,
    int OverdueInvoiceCount,
    decimal UnbilledWorkBase,
    string BaseCurrencyCode);

/// <summary>
/// Optional filter knobs surfaced on the Transactions tab. Null fields
/// mean "no filter on that axis"; the page passes null for the bits
/// the operator hasn't touched. Status is contains-match (free text)
/// against the derived status string so an operator can type "paid"
/// or "draft" without knowing the exact enum.
/// </summary>
public sealed record CustomerTransactionFilter(
    string? Type,    // "invoice" | "quote" | "sales_order" | null=all
    string? Status,
    DateOnly? From,
    DateOnly? To);

/// <summary>
/// One row in the unified per-customer transaction timeline. Status is
/// the derived "what should the operator see" label, not the raw column
/// value — invoice rows carry "paid" / "overdue" / "issued" / "draft"
/// instead of just the underlying invoices.status.
/// </summary>
public sealed record CustomerTransactionRow(
    DateOnly Date,
    string Type,           // "invoice" | "quote" | "sales_order"
    string DisplayNumber,
    Guid SourceId,
    string? Memo,
    decimal Amount,
    string CurrencyCode,
    string Status);
