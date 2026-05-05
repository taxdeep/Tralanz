using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M6 iter 4: Drop-ship Clearing aging projection. Per-item rollup of
/// posted bills (Dr Clearing from iter 2) vs posted invoice drop-ship
/// COGS (Cr Clearing from iter 3). Net = open clearing residual per
/// item. Operators use this to spot mismatches and trigger a write-off
/// when residuals are stale.
///
/// Aging is computed live (no bridge table) — every refresh re-walks
/// posted bills + invoices for drop-ship items. V1 trade-off: simple
/// to reason about, performant for typical drop-ship volumes; if
/// volumes grow large, a materialised projection is the natural V2.
/// </summary>
public interface IDropShipClearingAgingReader
{
    Task<IReadOnlyList<DropShipClearingAgingRow>> ListAsync(
        CompanyId companyId,
        bool hideBalanced,
        CancellationToken cancellationToken);
}

/// <summary>
/// One row per drop-ship item with any posted bill or invoice activity.
/// Quantities and amounts are signed:
///   net &gt; 0  → over-billed (vendor invoiced more than we sold)
///   net &lt; 0  → under-billed (we sold more than vendor invoiced — vendor catch-up due)
///   net = 0  → balanced (no action needed)
/// </summary>
public sealed record DropShipClearingAgingRow(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    decimal TotalBilledBase,
    decimal TotalQuantityBilled,
    decimal TotalInvoicedCogsBase,
    decimal TotalQuantityInvoiced,
    decimal NetClearingBase,
    DateOnly? OldestActivityDate,
    DateOnly? LatestActivityDate);
