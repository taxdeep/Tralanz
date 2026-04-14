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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
            query.AsOfDate,
            cancellationToken);

        return BalanceSheetReport.Create(
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
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
            query.CompanyId.Value,
            query.AsOfDate,
            baseCurrencyCode,
            rows);
    }

    private static async Task<string?> TryGetBaseCurrencyCodeAsync(
        PostgresCommandScope scope,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select base_currency_code
            from companies
            where id = @company_id
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is string value ? value.Trim() : null;
    }

    private static async Task<decimal> CalculateCurrentEarningsAsync(
        PostgresCommandScope scope,
        Guid companyId,
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

        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("as_of_date", asOfDate);

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is decimal value
            ? Math.Round(value, 6, MidpointRounding.ToEven)
            : 0m;
    }
}
