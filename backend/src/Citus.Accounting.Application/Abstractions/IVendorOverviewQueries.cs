namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Read-only aggregations for the Vendor detail page — AP-side mirror
/// of <see cref="ICustomerOverviewQueries"/>:
///   * Financial summary card (open balance, overdue count, open POs)
///   * Transactions tab — unified bills + purchase-orders + vendor-
///     credits timeline for one vendor with optional filters.
///
/// Open balance and overdue counts come from <c>ap_open_items</c>;
/// open-PO count is the count of POs not yet fully closed (draft /
/// open / partially-received). Expenses are intentionally excluded
/// from the timeline today — the expenses table doesn't carry a
/// vendor reference at this checkpoint, so they would only ever
/// show up as "—" rows. Add them once the column lands.
/// </summary>
public interface IVendorOverviewQueries
{
    Task<VendorFinancialSummary> GetFinancialSummaryAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<VendorTransactionRow>> ListTransactionsAsync(
        CompanyId companyId,
        Guid vendorId,
        VendorTransactionFilter filter,
        CancellationToken cancellationToken);
}

public sealed record VendorFinancialSummary(
    decimal OpenBalanceBase,
    int OverdueBillCount,
    int OpenPurchaseOrderCount,
    string BaseCurrencyCode);

public sealed record VendorTransactionFilter(
    string? Type,    // "bill" | "purchase_order" | "vendor_credit" | null=all
    string? Status,
    DateOnly? From,
    DateOnly? To);

public sealed record VendorTransactionRow(
    DateOnly Date,
    string Type,                 // "bill" | "purchase_order" | "vendor_credit"
    string DisplayNumber,
    Guid SourceId,
    string? Memo,
    decimal Amount,
    string CurrencyCode,
    string Status);
