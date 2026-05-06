using Citus.Accounting.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Counterparties;

/// <summary>
/// Postgres-backed read aggregations for the customer detail page.
/// Lives next to PostgreSqlCustomerStore so all customer-scoped reads
/// share one schema namespace and one connection factory.
///
/// All queries are parameterized on (company_id, customer_id) — no
/// company id, no result. Open-balance + overdue come straight from
/// <c>ar_open_items</c>, joined with <c>invoices</c> for the
/// transactions timeline so we can derive a richer status label
/// (paid / overdue / issued / draft) without forcing the page to
/// re-query per row.
/// </summary>
public sealed class PostgreSqlCustomerOverviewQueries(PostgreSqlConnectionFactory connections) : ICustomerOverviewQueries
{
    public async Task<CustomerFinancialSummary> GetFinancialSummaryAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Single round-trip: (a) company base currency, (b) open AR
        // aggregates. AR side filters on the two non-terminal statuses
        // ('open', 'partially_applied'); 'closed' rows are paid and
        // contribute nothing to balance / overdue.
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with company_row as (
                select base_currency_code from companies where id = @company_id
            ),
            ar_summary as (
                select
                    coalesce(sum(open_amount_base) filter (
                        where status in ('open','partially_applied')
                    ), 0) as open_balance_base,
                    count(distinct id) filter (
                        where status in ('open','partially_applied')
                          and due_date is not null
                          and due_date < current_date
                    ) as overdue_count
                from ar_open_items
                where company_id = @company_id
                  and customer_id = @customer_id
            )
            select
                (select base_currency_code from company_row) as base_currency_code,
                coalesce((select open_balance_base from ar_summary), 0) as open_balance_base,
                coalesce((select overdue_count from ar_summary), 0) as overdue_count;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Should never happen — even if the customer or company is
            // missing, the with-clause returns one row with nulls. But
            // be defensive so the page doesn't 500 on a freshly seeded
            // tenant with no AR data yet.
            return new CustomerFinancialSummary(0m, 0, 0m, string.Empty);
        }

        var baseCurrency = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var openBalance = reader.GetDecimal(1);
        var overdueCount = reader.GetInt32(2);

        return new CustomerFinancialSummary(
            OpenBalanceBase: openBalance,
            OverdueInvoiceCount: overdueCount,
            UnbilledWorkBase: 0m,        // wired when Task module ships
            BaseCurrencyCode: baseCurrency);
    }

    public async Task<IReadOnlyList<CustomerTransactionRow>> ListTransactionsAsync(
        CompanyId companyId,
        Guid customerId,
        CustomerTransactionFilter filter,
        CancellationToken cancellationToken)
    {
        // We always build the union over all three sources, then apply
        // the type filter at the outer level. Separately driving N
        // SQLs by type would multiply round-trips for no win — the
        // tables involved are small per-customer.
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with invoice_rows as (
                select
                    i.invoice_date as date,
                    'invoice'::text as type,
                    i.invoice_number as display_number,
                    i.id as source_id,
                    i.memo,
                    i.total_amount as amount,
                    i.document_currency_code as currency_code,
                    case
                        when i.status = 'draft' then 'draft'
                        when not exists (
                            select 1 from ar_open_items oi
                             where oi.company_id = i.company_id
                               and oi.source_type = 'invoice'
                               and oi.source_id = i.id
                               and oi.status in ('open','partially_applied')
                        ) then 'paid'
                        when i.due_date is not null and i.due_date < current_date then 'overdue'
                        else 'issued'
                    end as status
                from invoices i
                where i.company_id = @company_id
                  and i.customer_id = @customer_id
            ),
            quote_rows as (
                select
                    q.document_date as date,
                    'quote'::text as type,
                    q.quote_number as display_number,
                    q.id as source_id,
                    q.memo,
                    q.total_amount as amount,
                    q.document_currency_code as currency_code,
                    q.status
                from quotes q
                where q.company_id = @company_id
                  and q.customer_id = @customer_id
            ),
            sales_order_rows as (
                select
                    s.document_date as date,
                    'sales_order'::text as type,
                    s.sales_order_number as display_number,
                    s.id as source_id,
                    s.memo,
                    s.total_amount as amount,
                    s.document_currency_code as currency_code,
                    s.status
                from sales_orders s
                where s.company_id = @company_id
                  and s.customer_id = @customer_id
            ),
            unioned as (
                select * from invoice_rows
                union all
                select * from quote_rows
                union all
                select * from sales_order_rows
            )
            select date, type, display_number, source_id, memo, amount, currency_code, status
            from unioned
            where (@type is null or type = @type)
              and (@status is null or status ilike @status_pattern)
              and (@from_date is null or date >= @from_date)
              and (@to_date is null or date <= @to_date)
            order by date desc, display_number desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(filter.Type) ? DBNull.Value : filter.Type,
        });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(filter.Status) ? DBNull.Value : filter.Status,
        });
        // ilike pattern needs the % wrapping done server-side — we
        // duplicate the param so the SQL stays cleanly readable.
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

        var results = new List<CustomerTransactionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CustomerTransactionRow(
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
