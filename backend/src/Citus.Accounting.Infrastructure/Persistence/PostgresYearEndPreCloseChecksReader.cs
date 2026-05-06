using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M7 iter 4 implementation. Three independent COUNT(*) queries
/// against existing tables (no schema changes). Each uses the
/// already-bootstrapped indexes so the dashboard refresh stays fast
/// even on companies with high transaction volume.
///
/// Tables not yet bootstrapped (e.g. receipt_grir_bridge_lines on a
/// company that hasn't enabled the inventory module) safely return
/// zero via to_regclass — the dashboard then shows the check as
/// "not applicable" implicitly through the zero count.
/// </summary>
public sealed class PostgresYearEndPreCloseChecksReader : IYearEndPreCloseChecksReader
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresYearEndPreCloseChecksReader(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<YearEndPreCloseChecks> ReadAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        var grirCount = await CountGrIrAgedAsync(scope, companyId, cancellationToken);
        var dropShipCount = await CountDropShipClearingAgedAsync(scope, companyId, cancellationToken);
        var soCount = await CountSalesOrderBackorderAgedAsync(scope, companyId, cancellationToken);

        return new YearEndPreCloseChecks(
            GrIrAged: new YearEndPreCloseCheck(
                Title: "GR/IR bridge aged > 90 days",
                Description: "Receipts that have not been matched to a vendor bill (or vice versa) within 90 days. Each row keeps a balance on GR/IR Clearing that should reconcile before year-end close.",
                Count: grirCount,
                ResolutionHint: "Open the GR/IR workbench, match each row, or write off the variance to Purchase Price Variance."),
            DropShipClearingAged: new YearEndPreCloseCheck(
                Title: "Drop-ship Clearing residuals aged > 90 days",
                Description: "Drop-ship items where the bill side and invoice side have not zeroed each other out within 90 days. Open clearing balances distort margin reporting.",
                Count: dropShipCount,
                ResolutionHint: "Open the Drop-ship Clearing workbench at Inventory → Drop-ship Clearing and write off each per-item residual."),
            SalesOrderBackorderAged: new YearEndPreCloseCheck(
                Title: "Sales-order backorder lines aged > 30 days",
                Description: "Confirmed SOs with un-shipped backorder lines older than 30 days. Stuck backorders signal that customer commitments are unfunded by inventory plans.",
                Count: soCount,
                ResolutionHint: "Open Sales Orders, review the aged backorder rows, and either receipt the missing stock, cancel the SO, or convert to a drop-ship."));
    }

    private static async Task<int> CountGrIrAgedAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        // Inline the company id as a literal uuid because Postgres DO
        // blocks don't see Npgsql parameters. companyId comes from a
        // typed source (CompanyId.Value Guid) so there's no injection
        // surface. Pattern mirrors PostgresSourceDocumentDraftNumbering
        // .ScanOptionalTableMaxSeedAsync — same to_regclass guard,
        // same set_config / current_setting return path.
        await using var command = scope.CreateCommand(
            $"""
            do $$
            declare
              n integer := 0;
            begin
              if to_regclass('public.receipt_grir_bridge_lines') is not null then
                execute format($sql$
                  select count(*)
                    from receipt_grir_bridge_lines
                   where company_id = %L
                     and bridge_status in (
                       'eligible_not_posted',
                       'partially_posted',
                       'blocked_reconciliation_required',
                       'blocked_variance_required'
                     )
                     and refreshed_at < (now() - interval '90 days')
                $sql$, '{companyId:D}') into n;
              end if;
              perform set_config('citus.year_end_grir_check', n::text, true);
            end $$;
            select coalesce(nullif(current_setting('citus.year_end_grir_check', true), ''), '0')::integer;
            """);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private static async Task<int> CountDropShipClearingAgedAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        // Per-item residual + oldest-activity check, mirroring the
        // M6 iter 4 aging reader but reduced to a count of items
        // whose oldest activity is > 90 days old AND whose residual
        // is non-zero.
        await using var command = scope.CreateCommand(
            """
            with bill_side as (
              select bl.item_id,
                     coalesce(sum(bl.line_amount * b.fx_rate), 0) as billed_base,
                     min(b.bill_date) as oldest_bill_date
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
              select il.item_id,
                     coalesce(sum(il.quantity * coalesce(i.default_purchase_price, 0)), 0) as invoiced_base,
                     min(inv.invoice_date) as oldest_invoice_date
              from invoice_lines il
              join invoices inv on inv.id = il.invoice_id
              join inventory_items i on i.id = il.item_id
              where inv.company_id = @company_id
                and inv.status = 'posted'
                and i.item_kind = 'drop_ship'
              group by il.item_id
            )
            select count(*)::int
              from inventory_items i
              left join bill_side b on b.item_id = i.id
              left join invoice_side inv on inv.item_id = i.id
             where i.company_id = @company_id
               and i.item_kind = 'drop_ship'
               and i.is_active = true
               and abs(coalesce(b.billed_base, 0) - coalesce(inv.invoiced_base, 0)) > 0.005
               and least(b.oldest_bill_date, inv.oldest_invoice_date) < (current_date - interval '90 days');
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private static async Task<int> CountSalesOrderBackorderAgedAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        // Schema check — sales_order_lines may not exist if the SO
        // module hasn't been touched yet.
        await using var command = scope.CreateCommand(
            """
            do $$
            declare
              n integer := 0;
            begin
              if to_regclass('public.sales_order_lines') is not null
                 and to_regclass('public.sales_orders') is not null then
                execute format($sql$
                  select count(distinct sol.id)
                    from sales_order_lines sol
                    join sales_orders so on so.id = sol.sales_order_id
                   where so.company_id = %L
                     and sol.backorder_qty > 0
                     and so.status in ('confirmed', 'open')
                     and so.created_at < (now() - interval '30 days')
                $sql$, %L) into n;
              end if;
              perform set_config('citus.year_end_so_check', n::text, true);
            end $$;
            select coalesce(nullif(current_setting('citus.year_end_so_check', true), ''), '0')::integer;
            """.Replace("%L", $"'{companyId:D}'"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? 0 : Convert.ToInt32(result);
    }
}
