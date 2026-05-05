using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M6 iter 4 implementation. Live aggregation — no bridge table — so the
/// query walks every posted bill_line and posted invoice_line that
/// references a drop-ship item, sums the per-item totals, and
/// returns the residual.
///
/// V1 cost-basis convention (matches iter 3): invoice-side cost equals
/// qty × inventory_items.default_purchase_price. The bill-side amount
/// is the actual vendor-invoiced base amount. The residual is the gap
/// between expected (item.purchase_price) and actual vendor cost — an
/// operator-driven write-off lands in the next iter / iter-4-action.
/// </summary>
public sealed class PostgresDropShipClearingAgingReader : IDropShipClearingAgingReader
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresDropShipClearingAgingReader(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<DropShipClearingAgingRow>> ListAsync(
        CompanyId companyId,
        bool hideBalanced,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // The two CTEs walk bills and invoices independently so an item
        // that has only one side still surfaces (e.g. invoice posted but
        // bill not yet received → big credit residual). The outer LEFT
        // JOIN onto inventory_items keeps drop-ship items active in the
        // result even when one side is empty.
        await using var command = scope.CreateCommand(
            """
            with bill_side as (
              select
                bl.item_id,
                sum(bl.line_amount * b.fx_rate) as total_billed_base,
                sum(bl.quantity)                as total_qty_billed,
                min(b.bill_date)                as oldest_bill_date,
                max(b.bill_date)                as latest_bill_date
              from bill_lines bl
              join bills b on b.id = bl.bill_id
              join inventory_items i on i.id = bl.item_id
              where b.company_id = @company_id
                and b.status = 'posted'
                and i.item_kind = 'drop_ship'
                and bl.quantity is not null
              group by bl.item_id
            ),
            invoice_side as (
              select
                il.item_id,
                sum(il.quantity * coalesce(i.default_purchase_price, 0)) as total_invoiced_cogs_base,
                sum(il.quantity)                                          as total_qty_invoiced,
                min(inv.invoice_date)                                     as oldest_invoice_date,
                max(inv.invoice_date)                                     as latest_invoice_date
              from invoice_lines il
              join invoices inv on inv.id = il.invoice_id
              join inventory_items i on i.id = il.item_id
              where inv.company_id = @company_id
                and inv.status = 'posted'
                and i.item_kind = 'drop_ship'
              group by il.item_id
            )
            select
              i.id                                              as item_id,
              i.item_code,
              i.name,
              coalesce(b.total_billed_base, 0)                  as total_billed_base,
              coalesce(b.total_qty_billed, 0)                   as total_qty_billed,
              coalesce(inv.total_invoiced_cogs_base, 0)         as total_invoiced_cogs_base,
              coalesce(inv.total_qty_invoiced, 0)               as total_qty_invoiced,
              coalesce(b.total_billed_base, 0) - coalesce(inv.total_invoiced_cogs_base, 0) as net_clearing_base,
              least(b.oldest_bill_date, inv.oldest_invoice_date) as oldest_activity_date,
              greatest(b.latest_bill_date, inv.latest_invoice_date) as latest_activity_date
            from inventory_items i
            left join bill_side b   on b.item_id = i.id
            left join invoice_side inv on inv.item_id = i.id
            where i.company_id = @company_id
              and i.item_kind = 'drop_ship'
              and i.is_active = true
              and (
                coalesce(b.total_billed_base, 0) > 0
                or coalesce(inv.total_invoiced_cogs_base, 0) > 0
              )
              and (
                @hide_balanced = false
                or abs(coalesce(b.total_billed_base, 0) - coalesce(inv.total_invoiced_cogs_base, 0)) > 0.005
              )
            order by abs(coalesce(b.total_billed_base, 0) - coalesce(inv.total_invoiced_cogs_base, 0)) desc, i.item_code asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("hide_balanced", hideBalanced);

        var rows = new List<DropShipClearingAgingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DropShipClearingAgingRow(
                ItemId: reader.GetGuid(0),
                ItemCode: reader.GetString(1),
                ItemName: reader.GetString(2),
                TotalBilledBase: reader.GetDecimal(3),
                TotalQuantityBilled: reader.GetDecimal(4),
                TotalInvoicedCogsBase: reader.GetDecimal(5),
                TotalQuantityInvoiced: reader.GetDecimal(6),
                NetClearingBase: reader.GetDecimal(7),
                OldestActivityDate: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
                LatestActivityDate: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateOnly>(9)));
        }
        return rows;
    }
}
