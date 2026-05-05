using Citus.Accounting.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// Postgres-backed read aggregations for the Vendor detail page.
/// AP-side mirror of <see cref="PostgreSqlCustomerOverviewQueries"/>:
/// reads <c>ap_open_items</c> for balance / overdue counts, then a
/// UNION over <c>bills</c> + <c>ap_purchase_orders</c> + <c>vendor_credits</c>
/// for the transactions timeline.
///
/// Open-PO counting uses the heuristic "status not in (closed,
/// cancelled, fully_invoiced)" — kept loose so any new PO state the
/// posting engine adds tomorrow still counts as open until we
/// explicitly mark it terminal.
/// </summary>
public sealed class PostgreSqlVendorOverviewQueries(PostgreSqlConnectionFactory connections) : IVendorOverviewQueries
{
    public async Task<VendorFinancialSummary> GetFinancialSummaryAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with company_row as (
                select base_currency_code from companies where id = @company_id
            ),
            ap_summary as (
                select
                    coalesce(sum(open_amount_base) filter (
                        where status in ('open','partially_applied')
                    ), 0) as open_balance_base,
                    count(distinct id) filter (
                        where status in ('open','partially_applied')
                          and due_date is not null
                          and due_date < current_date
                    ) as overdue_count
                from ap_open_items
                where company_id = @company_id
                  and vendor_id = @vendor_id
            ),
            po_summary as (
                select count(*) as open_po_count
                from ap_purchase_orders
                where company_id = @company_id
                  and vendor_id = @vendor_id
                  and status not in ('closed','cancelled','fully_invoiced')
            )
            select
                (select base_currency_code from company_row) as base_currency_code,
                coalesce((select open_balance_base from ap_summary), 0) as open_balance_base,
                coalesce((select overdue_count from ap_summary), 0) as overdue_count,
                coalesce((select open_po_count from po_summary), 0) as open_po_count;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new VendorFinancialSummary(0m, 0, 0, string.Empty);
        }

        return new VendorFinancialSummary(
            OpenBalanceBase: reader.GetDecimal(1),
            OverdueBillCount: Convert.ToInt32(reader.GetValue(2)),
            OpenPurchaseOrderCount: Convert.ToInt32(reader.GetValue(3)),
            BaseCurrencyCode: reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
    }

    public async Task<IReadOnlyList<VendorTransactionRow>> ListTransactionsAsync(
        CompanyId companyId,
        Guid vendorId,
        VendorTransactionFilter filter,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with bill_rows as (
                select
                    b.bill_date as date,
                    'bill'::text as type,
                    b.bill_number as display_number,
                    b.id as source_id,
                    b.memo,
                    b.total_amount as amount,
                    b.document_currency_code as currency_code,
                    case
                        when b.status = 'draft' then 'draft'
                        when b.status = 'voided' then 'voided'
                        when not exists (
                            select 1 from ap_open_items oi
                             where oi.company_id = b.company_id
                               and oi.source_type = 'bill'
                               and oi.source_id = b.id
                               and oi.status in ('open','partially_applied')
                        ) then 'paid'
                        when b.due_date is not null and b.due_date < current_date then 'overdue'
                        else 'posted'
                    end as status
                from bills b
                where b.company_id = @company_id
                  and b.vendor_id = @vendor_id
            ),
            po_rows as (
                select
                    p.order_date as date,
                    'purchase_order'::text as type,
                    p.purchase_order_number as display_number,
                    p.id as source_id,
                    null::text as memo,
                    p.total_amount as amount,
                    p.document_currency_code as currency_code,
                    p.status
                from ap_purchase_orders p
                where p.company_id = @company_id
                  and p.vendor_id = @vendor_id
            ),
            credit_rows as (
                select
                    c.vendor_credit_date as date,
                    'vendor_credit'::text as type,
                    c.vendor_credit_number as display_number,
                    c.id as source_id,
                    null::text as memo,
                    c.total_amount as amount,
                    c.document_currency_code as currency_code,
                    c.status
                from vendor_credits c
                where c.company_id = @company_id
                  and c.vendor_id = @vendor_id
            ),
            unioned as (
                select * from bill_rows
                union all
                select * from po_rows
                union all
                select * from credit_rows
            )
            select date, type, display_number, source_id, memo, amount, currency_code, status
            from unioned
            where (@type is null or type = @type)
              and (@status is null or status ilike @status_pattern)
              and (@from_date is null or date >= @from_date)
              and (@to_date is null or date <= @to_date)
            order by date desc, display_number desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(filter.Type) ? DBNull.Value : filter.Type,
        });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(filter.Status) ? DBNull.Value : filter.Status,
        });
        command.Parameters.Add(new NpgsqlParameter("status_pattern", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(filter.Status)
                ? DBNull.Value
                : "%" + filter.Status + "%",
        });
        command.Parameters.Add(new NpgsqlParameter("from_date", NpgsqlDbType.Date)
        {
            Value = filter.From.HasValue ? filter.From.Value : (object)DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("to_date", NpgsqlDbType.Date)
        {
            Value = filter.To.HasValue ? filter.To.Value : (object)DBNull.Value,
        });

        var results = new List<VendorTransactionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new VendorTransactionRow(
                Date: reader.GetFieldValue<DateOnly>(0),
                Type: reader.GetString(1),
                DisplayNumber: reader.GetString(2),
                SourceId: reader.GetGuid(3),
                Memo: reader.IsDBNull(4) ? null : reader.GetString(4),
                Amount: reader.GetDecimal(5),
                CurrencyCode: reader.GetString(6),
                Status: reader.GetString(7)));
        }
        return results;
    }
}
