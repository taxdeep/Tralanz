using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresAccountingReportRepository : IAccountingReportRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresAccountingReportRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<TrialBalanceReport?> GetTrialBalanceAsync(
        GetTrialBalanceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var rows = new List<TrialBalanceAccountBalance>();

        await using var command = scope.CreateCommand(
            """
            select
              a.id,
              a.entity_number,
              a.code,
              a.name,
              a.root_type,
              a.detail_type,
              a.is_active,
              a.is_system,
              coalesce(sum(le.debit), 0)::numeric(20,6) as posted_debit_total,
              coalesce(sum(le.credit), 0)::numeric(20,6) as posted_credit_total
            from accounts a
            left join ledger_entries le
              on le.company_id = a.company_id
             and le.account_id = a.id
             and le.posting_date <= @as_of_date
            where a.company_id = @company_id
            group by
              a.id,
              a.entity_number,
              a.code,
              a.name,
              a.root_type,
              a.detail_type,
              a.is_active,
              a.is_system
            order by a.code, a.name;
            """);

        command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
        command.Parameters.AddWithValue("as_of_date", query.AsOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
                TrialBalanceAccountBalance.Create(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("entity_number")),
                    reader.GetString(reader.GetOrdinal("code")),
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.GetString(reader.GetOrdinal("root_type")),
                    reader.GetString(reader.GetOrdinal("detail_type")),
                    reader.GetBoolean(reader.GetOrdinal("is_active")),
                    reader.GetBoolean(reader.GetOrdinal("is_system")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_debit_total")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_credit_total"))));
        }

        return TrialBalanceReport.Create(
            query.CompanyId,
            query.AsOfDate,
            baseCurrencyCode,
            query.IncludeZeroBalanceAccounts,
            rows);
    }

    public async Task<IncomeStatementReport?> GetIncomeStatementAsync(
        GetIncomeStatementQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var rows = new List<IncomeStatementAccountAmount>();

        await using var command = scope.CreateCommand(
            """
            select
              a.id,
              a.entity_number,
              a.code,
              a.name,
              a.root_type,
              a.detail_type,
              a.is_active,
              a.is_system,
              coalesce(sum(le.debit), 0)::numeric(20,6) as posted_debit_total,
              coalesce(sum(le.credit), 0)::numeric(20,6) as posted_credit_total
            from accounts a
            left join ledger_entries le
              on le.company_id = a.company_id
             and le.account_id = a.id
             and le.posting_date >= @date_from
             and le.posting_date <= @date_to
            where a.company_id = @company_id
              and a.root_type in ('revenue', 'cost_of_sales', 'expense')
            group by
              a.id,
              a.entity_number,
              a.code,
              a.name,
              a.root_type,
              a.detail_type,
              a.is_active,
              a.is_system
            order by a.code, a.name;
            """);

        command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
        command.Parameters.AddWithValue("date_from", query.DateFrom);
        command.Parameters.AddWithValue("date_to", query.DateTo);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
                IncomeStatementAccountAmount.Create(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("entity_number")),
                    reader.GetString(reader.GetOrdinal("code")),
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.GetString(reader.GetOrdinal("root_type")),
                    reader.GetString(reader.GetOrdinal("detail_type")),
                    reader.GetBoolean(reader.GetOrdinal("is_active")),
                    reader.GetBoolean(reader.GetOrdinal("is_system")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_debit_total")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_credit_total"))));
        }

        return IncomeStatementReport.Create(
            query.CompanyId,
            query.DateFrom,
            query.DateTo,
            baseCurrencyCode,
            query.IncludeZeroBalanceAccounts,
            rows);
    }

    public async Task<BalanceSheetReport?> GetBalanceSheetAsync(
        GetBalanceSheetQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var rows = new List<BalanceSheetAccountAmount>();

        await using (var command = scope.CreateCommand(
                         """
                         select
                           a.id,
                           a.entity_number,
                           a.code,
                           a.name,
                           a.root_type,
                           a.detail_type,
                           a.is_active,
                           a.is_system,
                           coalesce(sum(le.debit), 0)::numeric(20,6) as posted_debit_total,
                           coalesce(sum(le.credit), 0)::numeric(20,6) as posted_credit_total
                         from accounts a
                         left join ledger_entries le
                           on le.company_id = a.company_id
                          and le.account_id = a.id
                          and le.posting_date <= @as_of_date
                         where a.company_id = @company_id
                           and a.root_type in ('asset', 'liability', 'equity')
                         group by
                           a.id,
                           a.entity_number,
                           a.code,
                           a.name,
                           a.root_type,
                           a.detail_type,
                           a.is_active,
                           a.is_system
                         order by a.code, a.name;
                         """))
        {
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("as_of_date", query.AsOfDate);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(
                    BalanceSheetAccountAmount.Create(
                        reader.GetGuid(reader.GetOrdinal("id")),
                        reader.GetString(reader.GetOrdinal("entity_number")),
                        reader.GetString(reader.GetOrdinal("code")),
                        reader.GetString(reader.GetOrdinal("name")),
                        reader.GetString(reader.GetOrdinal("root_type")),
                        reader.GetString(reader.GetOrdinal("detail_type")),
                        reader.GetBoolean(reader.GetOrdinal("is_active")),
                        reader.GetBoolean(reader.GetOrdinal("is_system")),
                        reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_debit_total")),
                        reader.GetFieldValue<decimal>(reader.GetOrdinal("posted_credit_total"))));
            }
        }

        var currentEarnings = await CalculateCurrentEarningsAsync(
            scope,
            query.CompanyId,
            query.AsOfDate,
            cancellationToken);

        return BalanceSheetReport.Create(
            query.CompanyId,
            query.AsOfDate,
            baseCurrencyCode,
            query.IncludeZeroBalanceAccounts,
            rows,
            currentEarnings);
    }

    public async Task<ArAgingReport?> GetArAgingAsync(
        GetArAgingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var rows = new List<ArAgingOpenItemAmount>();

        await using var command = scope.CreateCommand(
            """
            with source_open_items as (
              select
                oi.id as open_item_id,
                oi.customer_id,
                c.entity_number as customer_entity_number,
                c.display_name as customer_display_name,
                c.is_active as customer_is_active,
                oi.source_type,
                oi.source_id,
                coalesce(i.invoice_number, cn.credit_note_number, oi.source_id::text) as display_number,
                coalesce(i.invoice_date, cn.credit_note_date, oi.due_date, @as_of_date) as document_date,
                oi.due_date,
                oi.document_currency_code,
                oi.base_currency_code,
                oi.balance_side,
                oi.status,
                oi.original_amount_tx,
                oi.original_amount_base,
                case
                  when oi.source_type = 'invoice' then i.posted_at::date
                  when oi.source_type = 'credit_note' then cn.posted_at::date
                  else null
                end as source_posted_date
              from ar_open_items oi
              inner join customers c
                on c.company_id = oi.company_id
               and c.id = oi.customer_id
              left join invoices i
                on oi.source_type = 'invoice'
               and i.company_id = oi.company_id
               and i.id = oi.source_id
              left join credit_notes cn
                on oi.source_type = 'credit_note'
               and cn.company_id = oi.company_id
               and cn.id = oi.source_id
              where oi.company_id = @company_id
                and oi.source_type in ('invoice', 'credit_note')
            ),
            applied_as_of as (
              select
                sa.target_open_item_id,
                coalesce(sum(sa.applied_amount_tx), 0)::numeric(20,6) as applied_amount_tx,
                coalesce(sum(sa.applied_amount_base), 0)::numeric(20,6) as applied_amount_base
              from settlement_applications sa
              left join receive_payments rp
                on sa.source_type = 'receive_payment'
               and rp.company_id = sa.company_id
               and rp.id = sa.source_id
              left join credit_applications ca
                on sa.source_type = 'credit_application'
               and ca.company_id = sa.company_id
               and ca.id = sa.source_id
              where sa.company_id = @company_id
                and sa.target_open_item_type = 'ar_open_item'
                and coalesce(rp.payment_date, ca.application_date) <= @as_of_date
              group by sa.target_open_item_id
            )
            select
              oi.open_item_id,
              oi.customer_id,
              oi.customer_entity_number,
              oi.customer_display_name,
              oi.customer_is_active,
              oi.source_type,
              oi.source_id,
              oi.display_number,
              oi.document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.balance_side,
              oi.status,
              oi.original_amount_tx,
              oi.original_amount_base,
              greatest(oi.original_amount_tx - coalesce(app.applied_amount_tx, 0), 0)::numeric(20,6) as open_amount_tx,
              greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0)::numeric(20,6) as open_amount_base
            from source_open_items oi
            left join applied_as_of app
              on app.target_open_item_id = oi.open_item_id
            where oi.source_posted_date is not null
              and oi.source_posted_date <= @as_of_date
              and greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0) > 0
            order by
              oi.customer_display_name asc,
              oi.customer_entity_number asc,
              oi.due_date asc nulls first,
              oi.document_date asc,
              oi.display_number asc;
            """);

        command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
        command.Parameters.AddWithValue("as_of_date", query.AsOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
                ArAgingOpenItemAmount.Create(
                    reader.GetGuid(reader.GetOrdinal("open_item_id")),
                    reader.GetGuid(reader.GetOrdinal("customer_id")),
                    reader.GetString(reader.GetOrdinal("customer_entity_number")),
                    reader.GetString(reader.GetOrdinal("customer_display_name")),
                    reader.GetBoolean(reader.GetOrdinal("customer_is_active")),
                    reader.GetString(reader.GetOrdinal("source_type")),
                    reader.GetGuid(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("display_number")),
                    reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                    reader.IsDBNull(reader.GetOrdinal("due_date"))
                        ? null
                        : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
                    reader.GetString(reader.GetOrdinal("document_currency_code")),
                    reader.GetString(reader.GetOrdinal("base_currency_code")),
                    reader.GetString(reader.GetOrdinal("balance_side")),
                    reader.GetString(reader.GetOrdinal("status")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("original_amount_tx")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("original_amount_base")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("open_amount_tx")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("open_amount_base")),
                    query.AsOfDate));
        }

        return ArAgingReport.Create(
            query.CompanyId,
            query.AsOfDate,
            baseCurrencyCode,
            rows);
    }

    public async Task<ApAgingReport?> GetApAgingAsync(
        GetApAgingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var rows = new List<ApAgingOpenItemAmount>();

        await using var command = scope.CreateCommand(
            """
            with source_open_items as (
              select
                oi.id as open_item_id,
                oi.vendor_id,
                v.entity_number as vendor_entity_number,
                v.display_name as vendor_display_name,
                v.is_active as vendor_is_active,
                oi.source_type,
                oi.source_id,
                coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as display_number,
                coalesce(b.bill_date, vc.vendor_credit_date, oi.due_date, @as_of_date) as document_date,
                oi.due_date,
                oi.document_currency_code,
                oi.base_currency_code,
                oi.balance_side,
                oi.status,
                oi.original_amount_tx,
                oi.original_amount_base,
                case
                  when oi.source_type = 'bill' then b.posted_at::date
                  when oi.source_type = 'vendor_credit' then vc.posted_at::date
                  else null
                end as source_posted_date
              from ap_open_items oi
              inner join vendors v
                on v.company_id = oi.company_id
               and v.id = oi.vendor_id
              left join bills b
                on oi.source_type = 'bill'
               and b.company_id = oi.company_id
               and b.id = oi.source_id
              left join vendor_credits vc
                on oi.source_type = 'vendor_credit'
               and vc.company_id = oi.company_id
               and vc.id = oi.source_id
              where oi.company_id = @company_id
                and oi.source_type in ('bill', 'vendor_credit')
            ),
            applied_as_of as (
              select
                sa.target_open_item_id,
                coalesce(sum(sa.applied_amount_tx), 0)::numeric(20,6) as applied_amount_tx,
                coalesce(sum(sa.applied_amount_base), 0)::numeric(20,6) as applied_amount_base
              from settlement_applications sa
              left join pay_bills pb
                on sa.source_type = 'pay_bill'
               and pb.company_id = sa.company_id
               and pb.id = sa.source_id
              left join vendor_credit_applications vca
                on sa.source_type = 'vendor_credit_application'
               and vca.company_id = sa.company_id
               and vca.id = sa.source_id
              where sa.company_id = @company_id
                and sa.target_open_item_type = 'ap_open_item'
                and coalesce(pb.payment_date, vca.application_date) <= @as_of_date
              group by sa.target_open_item_id
            )
            select
              oi.open_item_id,
              oi.vendor_id,
              oi.vendor_entity_number,
              oi.vendor_display_name,
              oi.vendor_is_active,
              oi.source_type,
              oi.source_id,
              oi.display_number,
              oi.document_date,
              oi.due_date,
              oi.document_currency_code,
              oi.base_currency_code,
              oi.balance_side,
              oi.status,
              oi.original_amount_tx,
              oi.original_amount_base,
              greatest(oi.original_amount_tx - coalesce(app.applied_amount_tx, 0), 0)::numeric(20,6) as open_amount_tx,
              greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0)::numeric(20,6) as open_amount_base
            from source_open_items oi
            left join applied_as_of app
              on app.target_open_item_id = oi.open_item_id
            where oi.source_posted_date is not null
              and oi.source_posted_date <= @as_of_date
              and greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0) > 0
            order by
              oi.vendor_display_name asc,
              oi.vendor_entity_number asc,
              oi.due_date asc nulls first,
              oi.document_date asc,
              oi.display_number asc;
            """);

        command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
        command.Parameters.AddWithValue("as_of_date", query.AsOfDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
                ApAgingOpenItemAmount.Create(
                    reader.GetGuid(reader.GetOrdinal("open_item_id")),
                    reader.GetGuid(reader.GetOrdinal("vendor_id")),
                    reader.GetString(reader.GetOrdinal("vendor_entity_number")),
                    reader.GetString(reader.GetOrdinal("vendor_display_name")),
                    reader.GetBoolean(reader.GetOrdinal("vendor_is_active")),
                    reader.GetString(reader.GetOrdinal("source_type")),
                    reader.GetGuid(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("display_number")),
                    reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                    reader.IsDBNull(reader.GetOrdinal("due_date"))
                        ? null
                        : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
                    reader.GetString(reader.GetOrdinal("document_currency_code")),
                    reader.GetString(reader.GetOrdinal("base_currency_code")),
                    reader.GetString(reader.GetOrdinal("balance_side")),
                    reader.GetString(reader.GetOrdinal("status")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("original_amount_tx")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("original_amount_base")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("open_amount_tx")),
                    reader.GetFieldValue<decimal>(reader.GetOrdinal("open_amount_base")),
                    query.AsOfDate));
        }

        return ApAgingReport.Create(
            query.CompanyId,
            query.AsOfDate,
            baseCurrencyCode,
            rows);
    }

    public async Task<SalesCashFlowReport?> GetSalesCashFlowAsync(
        GetSalesCashFlowQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        // Window: 10 historical months + current + 3 forecast = 14 buckets.
        var asOfMonthStart = new DateOnly(query.AsOfDate.Year, query.AsOfDate.Month, 1);
        var fromMonthStart = asOfMonthStart.AddMonths(-10);
        var lastForecastMonthStart = asOfMonthStart.AddMonths(3);
        var forecastWindowEnd = lastForecastMonthStart.AddMonths(1).AddDays(-1);

        // Past + current: posted receive-payments grouped by payment_date
        // month. fx_rate is stored on each row (document → base) so the
        // sum is straight base-currency.
        var receivedByMonth = new Dictionary<(int Year, int Month), decimal>();
        await using (var command = scope.CreateCommand(
            """
            select date_trunc('month', rp.payment_date)::date as month_start,
                   coalesce(sum(rp.total_amount * rp.fx_rate), 0)::numeric(20,6) as received_base
              from receive_payments rp
             where rp.company_id = @company_id
               and rp.status = 'posted'
               and rp.payment_date >= @from_date
               and rp.payment_date <= @to_date
             group by 1
             order by 1;
            """))
        {
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("from_date", fromMonthStart);
            command.Parameters.AddWithValue("to_date", asOfMonthStart.AddMonths(1).AddDays(-1));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var monthStart = reader.GetFieldValue<DateOnly>(0);
                var amount = reader.GetDecimal(1);
                receivedByMonth[(monthStart.Year, monthStart.Month)] = amount;
            }
        }

        // Forecast: open AR balance (signed, base) by due-date month.
        // settlement_applications already considered up to as-of date so
        // partial payments on existing invoices reduce the forecast.
        var forecastByMonth = new Dictionary<(int Year, int Month), decimal>();
        await using (var command = scope.CreateCommand(
            """
            with applied_as_of as (
              select sa.target_open_item_id,
                     coalesce(sum(sa.applied_amount_base), 0)::numeric(20,6) as applied_amount_base
                from settlement_applications sa
                left join receive_payments rp
                  on sa.source_type = 'receive_payment'
                 and rp.company_id = sa.company_id
                 and rp.id = sa.source_id
                left join credit_applications ca
                  on sa.source_type = 'credit_application'
                 and ca.company_id = sa.company_id
                 and ca.id = sa.source_id
               where sa.company_id = @company_id
                 and sa.target_open_item_type = 'ar_open_item'
                 and coalesce(rp.payment_date, ca.application_date) <= @as_of_date
               group by sa.target_open_item_id
            )
            select date_trunc('month', oi.due_date)::date as month_start,
                   coalesce(
                     sum(
                       greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0)
                       * case when oi.balance_side = 'credit' then -1 else 1 end
                     ),
                     0
                   )::numeric(20,6) as forecast_base
              from ar_open_items oi
              left join applied_as_of app
                on app.target_open_item_id = oi.id
              left join invoices i
                on oi.source_type = 'invoice'
               and i.company_id = oi.company_id
               and i.id = oi.source_id
              left join credit_notes cn
                on oi.source_type = 'credit_note'
               and cn.company_id = oi.company_id
               and cn.id = oi.source_id
             where oi.company_id = @company_id
               and oi.source_type in ('invoice', 'credit_note')
               and oi.due_date is not null
               and oi.due_date >= @forecast_from
               and oi.due_date <= @forecast_to
               and (
                 (oi.source_type = 'invoice' and i.posted_at is not null)
                 or
                 (oi.source_type = 'credit_note' and cn.posted_at is not null)
               )
               and greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0) > 0
             group by 1
             order by 1;
            """))
        {
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("as_of_date", query.AsOfDate);
            command.Parameters.AddWithValue("forecast_from", asOfMonthStart.AddMonths(1));
            command.Parameters.AddWithValue("forecast_to", forecastWindowEnd);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var monthStart = reader.GetFieldValue<DateOnly>(0);
                var amount = reader.GetDecimal(1);
                forecastByMonth[(monthStart.Year, monthStart.Month)] = amount;
            }
        }

        // Materialize all 14 buckets so empty months still render in the
        // UI. Past / current → received column; forecast → forecast column.
        var buckets = new List<SalesCashFlowMonthBucket>(14);
        for (var i = 0; i < 14; i++)
        {
            var monthStart = fromMonthStart.AddMonths(i);
            var key = (monthStart.Year, monthStart.Month);
            var isCurrent = monthStart == asOfMonthStart;
            var isForecast = monthStart > asOfMonthStart;

            buckets.Add(new SalesCashFlowMonthBucket
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                MonthStart = monthStart,
                IsForecast = isForecast,
                IsCurrent = isCurrent,
                ReceivedAmountBase = isForecast ? 0m :
                    (receivedByMonth.TryGetValue(key, out var rcv) ? rcv : 0m),
                ForecastAmountBase = isForecast ?
                    (forecastByMonth.TryGetValue(key, out var fcs) ? fcs : 0m) : 0m,
            });
        }

        return new SalesCashFlowReport
        {
            CompanyId = query.CompanyId,
            AsOfDate = query.AsOfDate,
            BaseCurrencyCode = baseCurrencyCode!.Trim().ToUpperInvariant(),
            Months = buckets,
        };
    }

    public async Task<IncomeOverTimeReport?> GetIncomeOverTimeAsync(
        GetIncomeOverTimeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ToDate < query.FromDate)
        {
            throw new ArgumentException("ToDate must be on or after FromDate.", nameof(query));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var fromMonthStart = new DateOnly(query.FromDate.Year, query.FromDate.Month, 1);
        var toMonthStart = new DateOnly(query.ToDate.Year, query.ToDate.Month, 1);

        async Task<Dictionary<(int Year, int Month), decimal>> RunWindowAsync(
            DateOnly windowFrom,
            DateOnly windowTo)
        {
            var windowStart = new DateOnly(windowFrom.Year, windowFrom.Month, 1);
            var windowEnd = new DateOnly(windowTo.Year, windowTo.Month, 1)
                .AddMonths(1).AddDays(-1);

            var byMonth = new Dictionary<(int Year, int Month), decimal>();
            await using var command = scope.CreateCommand(
                """
                select date_trunc('month', i.invoice_date)::date as month_start,
                       coalesce(sum(i.total_amount * i.fx_rate), 0)::numeric(20,6) as amount_base
                  from invoices i
                 where i.company_id = @company_id
                   and i.status = 'posted'
                   and i.invoice_date >= @from_date
                   and i.invoice_date <= @to_date
                 group by 1
                 order by 1;
                """);
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("from_date", windowStart);
            command.Parameters.AddWithValue("to_date", windowEnd);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var monthStart = reader.GetFieldValue<DateOnly>(0);
                byMonth[(monthStart.Year, monthStart.Month)] = reader.GetDecimal(1);
            }
            return byMonth;
        }

        var current = await RunWindowAsync(fromMonthStart, toMonthStart);
        var previous = query.CompareToPreviousYear
            ? await RunWindowAsync(fromMonthStart.AddYears(-1), toMonthStart.AddYears(-1))
            : new Dictionary<(int Year, int Month), decimal>();

        static IReadOnlyList<IncomeOverTimeMonthBucket> Materialize(
            DateOnly start,
            DateOnly end,
            Dictionary<(int Year, int Month), decimal> data)
        {
            var months = new List<IncomeOverTimeMonthBucket>();
            for (var month = start; month <= end; month = month.AddMonths(1))
            {
                var key = (month.Year, month.Month);
                months.Add(new IncomeOverTimeMonthBucket
                {
                    Year = month.Year,
                    Month = month.Month,
                    MonthStart = month,
                    AmountBase = data.TryGetValue(key, out var amount) ? amount : 0m,
                });
            }
            return months;
        }

        return new IncomeOverTimeReport
        {
            CompanyId = query.CompanyId,
            FromDate = query.FromDate,
            ToDate = query.ToDate,
            BaseCurrencyCode = baseCurrencyCode!.Trim().ToUpperInvariant(),
            CompareToPreviousYear = query.CompareToPreviousYear,
            Months = Materialize(fromMonthStart, toMonthStart, current),
            PreviousYearMonths = query.CompareToPreviousYear
                ? Materialize(fromMonthStart.AddYears(-1), toMonthStart.AddYears(-1), previous)
                : Array.Empty<IncomeOverTimeMonthBucket>(),
        };
    }

    public async Task<ExpenseCashOutflowReport?> GetExpenseCashOutflowAsync(
        GetExpenseCashOutflowQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var asOfMonthStart = new DateOnly(query.AsOfDate.Year, query.AsOfDate.Month, 1);
        var fromMonthStart = asOfMonthStart.AddMonths(-10);
        var lastForecastMonthStart = asOfMonthStart.AddMonths(3);
        var forecastWindowEnd = lastForecastMonthStart.AddMonths(1).AddDays(-1);

        // Past + current: posted pay_bills and posted expenses, both
        // grouped by their respective payment_date. UNION ALL keeps
        // the SQL straight — there is no risk of duplicate rows
        // across the two source tables.
        var paidByMonth = new Dictionary<(int Year, int Month), decimal>();
        await using (var command = scope.CreateCommand(
            """
            with paid_rows as (
              select pb.payment_date as paid_date,
                     (pb.total_amount * pb.fx_rate)::numeric(20,6) as paid_base
                from pay_bills pb
               where pb.company_id = @company_id
                 and pb.status = 'posted'
                 and pb.payment_date >= @from_date
                 and pb.payment_date <= @to_date
              union all
              select e.payment_date as paid_date,
                     (e.total_amount * e.fx_rate)::numeric(20,6) as paid_base
                from expenses e
               where e.company_id = @company_id
                 and e.status = 'posted'
                 and e.payment_date >= @from_date
                 and e.payment_date <= @to_date
            )
            select date_trunc('month', paid_date)::date as month_start,
                   coalesce(sum(paid_base), 0)::numeric(20,6) as paid_base
              from paid_rows
             group by 1
             order by 1;
            """))
        {
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("from_date", fromMonthStart);
            command.Parameters.AddWithValue("to_date", asOfMonthStart.AddMonths(1).AddDays(-1));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var monthStart = reader.GetFieldValue<DateOnly>(0);
                paidByMonth[(monthStart.Year, monthStart.Month)] = reader.GetDecimal(1);
            }
        }

        // Forecast: open AP balance (signed, base) by due-date month.
        // Mirrors the AR-side forecast — pay_bills + vendor_credit_applications
        // applied through the as-of date already reduce the balance.
        var forecastByMonth = new Dictionary<(int Year, int Month), decimal>();
        await using (var command = scope.CreateCommand(
            """
            with applied_as_of as (
              select sa.target_open_item_id,
                     coalesce(sum(sa.applied_amount_base), 0)::numeric(20,6) as applied_amount_base
                from settlement_applications sa
                left join pay_bills pb
                  on sa.source_type = 'pay_bill'
                 and pb.company_id = sa.company_id
                 and pb.id = sa.source_id
                left join vendor_credit_applications vca
                  on sa.source_type = 'vendor_credit_application'
                 and vca.company_id = sa.company_id
                 and vca.id = sa.source_id
               where sa.company_id = @company_id
                 and sa.target_open_item_type = 'ap_open_item'
                 and coalesce(pb.payment_date, vca.application_date) <= @as_of_date
               group by sa.target_open_item_id
            )
            select date_trunc('month', oi.due_date)::date as month_start,
                   coalesce(
                     sum(
                       greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0)
                       * case when oi.balance_side = 'debit' then -1 else 1 end
                     ),
                     0
                   )::numeric(20,6) as forecast_base
              from ap_open_items oi
              left join applied_as_of app
                on app.target_open_item_id = oi.id
              left join bills b
                on oi.source_type = 'bill'
               and b.company_id = oi.company_id
               and b.id = oi.source_id
              left join vendor_credits vc
                on oi.source_type = 'vendor_credit'
               and vc.company_id = oi.company_id
               and vc.id = oi.source_id
             where oi.company_id = @company_id
               and oi.source_type in ('bill', 'vendor_credit')
               and oi.due_date is not null
               and oi.due_date >= @forecast_from
               and oi.due_date <= @forecast_to
               and (
                 (oi.source_type = 'bill' and b.posted_at is not null)
                 or
                 (oi.source_type = 'vendor_credit' and vc.posted_at is not null)
               )
               and greatest(oi.original_amount_base - coalesce(app.applied_amount_base, 0), 0) > 0
             group by 1
             order by 1;
            """))
        {
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("as_of_date", query.AsOfDate);
            command.Parameters.AddWithValue("forecast_from", asOfMonthStart.AddMonths(1));
            command.Parameters.AddWithValue("forecast_to", forecastWindowEnd);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var monthStart = reader.GetFieldValue<DateOnly>(0);
                forecastByMonth[(monthStart.Year, monthStart.Month)] = reader.GetDecimal(1);
            }
        }

        var buckets = new List<ExpenseCashOutflowMonthBucket>(14);
        for (var i = 0; i < 14; i++)
        {
            var monthStart = fromMonthStart.AddMonths(i);
            var key = (monthStart.Year, monthStart.Month);
            var isCurrent = monthStart == asOfMonthStart;
            var isForecast = monthStart > asOfMonthStart;

            buckets.Add(new ExpenseCashOutflowMonthBucket
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                MonthStart = monthStart,
                IsForecast = isForecast,
                IsCurrent = isCurrent,
                PaidAmountBase = isForecast ? 0m :
                    (paidByMonth.TryGetValue(key, out var pd) ? pd : 0m),
                ForecastAmountBase = isForecast ?
                    (forecastByMonth.TryGetValue(key, out var fcs) ? fcs : 0m) : 0m,
            });
        }

        return new ExpenseCashOutflowReport
        {
            CompanyId = query.CompanyId,
            AsOfDate = query.AsOfDate,
            BaseCurrencyCode = baseCurrencyCode!.Trim().ToUpperInvariant(),
            Months = buckets,
        };
    }

    public async Task<ExpenseOverTimeReport?> GetExpenseOverTimeAsync(
        GetExpenseOverTimeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ToDate < query.FromDate)
        {
            throw new ArgumentException("ToDate must be on or after FromDate.", nameof(query));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var baseCurrencyCode = await TryGetBaseCurrencyCodeAsync(
            scope,
            query.CompanyId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return null;
        }

        var fromMonthStart = new DateOnly(query.FromDate.Year, query.FromDate.Month, 1);
        var toMonthStart = new DateOnly(query.ToDate.Year, query.ToDate.Month, 1);

        async Task<Dictionary<(int Year, int Month), decimal>> RunWindowAsync(
            DateOnly windowFrom,
            DateOnly windowTo)
        {
            var windowStart = new DateOnly(windowFrom.Year, windowFrom.Month, 1);
            var windowEnd = new DateOnly(windowTo.Year, windowTo.Month, 1)
                .AddMonths(1).AddDays(-1);

            var byMonth = new Dictionary<(int Year, int Month), decimal>();
            await using var command = scope.CreateCommand(
                """
                with cost_rows as (
                  select b.bill_date as cost_date,
                         (b.total_amount * b.fx_rate)::numeric(20,6) as amount_base
                    from bills b
                   where b.company_id = @company_id
                     and b.status = 'posted'
                     and b.bill_date >= @from_date
                     and b.bill_date <= @to_date
                  union all
                  select e.payment_date as cost_date,
                         (e.total_amount * e.fx_rate)::numeric(20,6) as amount_base
                    from expenses e
                   where e.company_id = @company_id
                     and e.status = 'posted'
                     and e.payment_date >= @from_date
                     and e.payment_date <= @to_date
                )
                select date_trunc('month', cost_date)::date as month_start,
                       coalesce(sum(amount_base), 0)::numeric(20,6) as amount_base
                  from cost_rows
                 group by 1
                 order by 1;
                """);
            command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
            command.Parameters.AddWithValue("from_date", windowStart);
            command.Parameters.AddWithValue("to_date", windowEnd);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var monthStart = reader.GetFieldValue<DateOnly>(0);
                byMonth[(monthStart.Year, monthStart.Month)] = reader.GetDecimal(1);
            }
            return byMonth;
        }

        var current = await RunWindowAsync(fromMonthStart, toMonthStart);
        var previous = query.CompareToPreviousYear
            ? await RunWindowAsync(fromMonthStart.AddYears(-1), toMonthStart.AddYears(-1))
            : new Dictionary<(int Year, int Month), decimal>();

        static IReadOnlyList<ExpenseOverTimeMonthBucket> Materialize(
            DateOnly start,
            DateOnly end,
            Dictionary<(int Year, int Month), decimal> data)
        {
            var months = new List<ExpenseOverTimeMonthBucket>();
            for (var month = start; month <= end; month = month.AddMonths(1))
            {
                var key = (month.Year, month.Month);
                months.Add(new ExpenseOverTimeMonthBucket
                {
                    Year = month.Year,
                    Month = month.Month,
                    MonthStart = month,
                    AmountBase = data.TryGetValue(key, out var amount) ? amount : 0m,
                });
            }
            return months;
        }

        return new ExpenseOverTimeReport
        {
            CompanyId = query.CompanyId,
            FromDate = query.FromDate,
            ToDate = query.ToDate,
            BaseCurrencyCode = baseCurrencyCode!.Trim().ToUpperInvariant(),
            CompareToPreviousYear = query.CompareToPreviousYear,
            Months = Materialize(fromMonthStart, toMonthStart, current),
            PreviousYearMonths = query.CompareToPreviousYear
                ? Materialize(fromMonthStart.AddYears(-1), toMonthStart.AddYears(-1), previous)
                : Array.Empty<ExpenseOverTimeMonthBucket>(),
        };
    }

    private static async Task<string?> TryGetBaseCurrencyCodeAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select base_currency_code
            from companies
            where id = @company_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is string value ? value.Trim() : null;
    }

    private static async Task<decimal> CalculateCurrentEarningsAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select coalesce(
              sum(
                le.credit - le.debit
              ),
              0
            )::numeric(20,6) as current_earnings
            from accounts a
            left join ledger_entries le
              on le.company_id = a.company_id
             and le.account_id = a.id
             and le.posting_date <= @as_of_date
            where a.company_id = @company_id
              and a.root_type in ('revenue', 'cost_of_sales', 'expense');
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is decimal value
            ? Math.Round(value, 6, MidpointRounding.ToEven)
            : 0m;
    }
}
