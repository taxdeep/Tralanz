using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using Npgsql;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Infrastructure.PostgreSQL.Tasks;

/// <summary>
/// Live aggregation — no materialised view, no cache. The two cost
/// CTEs scan bill_lines and expense_lines filtered by company_id and
/// non-null task_id; the partial index added by
/// <see cref="PostgresTaskLinkSchemaInitializer"/> keeps the scans
/// cheap as long as task attribution stays sparse.
///
/// FX model is GL-aligned: each amount is converted at the rate the
/// document was posted at, not the task's date. The task itself
/// doesn't touch the GL — the invoice / bill / expense does.
///
/// <list type="bullet">
///   <item><b>Billable side (revenue)</b>:
///     <list type="bullet">
///       <item>Billed task → use the linked invoice's <c>fx_rate</c>
///         (the rate at which Cr Revenue / Dr AR actually hit the
///         ledger). GL-truth.</item>
///       <item>Unbilled task (open / completed, mode=operational) →
///         use today's <c>fx_rates_daily</c> spot rate. There's no
///         GL touch yet; this is a projection of "if we billed
///         today, what would the base-currency revenue be?".</item>
///       <item>Task currency == base currency → rate = 1.</item>
///       <item>None of the above resolves → rate = 1, FxResolved =
///         false. UI badges the row.</item>
///     </list>
///   </item>
///   <item><b>Cost side</b>: each <c>bill_line</c> / <c>expense_line</c>
///     is multiplied by its parent doc's <c>fx_rate</c> before
///     summing. So a USD bill posted at 1.36 CAD contributes 136 CAD
///     to the cost base regardless of today's rate or the task's
///     date — that's the value the GL has booked. <c>DirectCost</c>
///     (source) sums raw line amounts across whatever currencies the
///     cost docs were in (cosmetic / approximate);
///     <c>DirectCostBase</c> is the GL-honest base-currency sum.</item>
/// </list>
/// </summary>
public sealed class PostgreSqlTaskMarginReportService(PostgreSqlConnectionFactory connections) : ITaskMarginReportService
{
    public async Task<TaskMarginReportResult> GetReportAsync(
        TaskMarginReportQuery query,
        CancellationToken cancellationToken)
    {
        if (query.CompanyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required for the task margin report.");
        }
        if (string.IsNullOrWhiteSpace(query.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Base currency code is required for the task margin report.");
        }

        var take = Math.Clamp(query.Take, 1, 500);
        var skip = Math.Max(0, query.Skip);
        var baseCurrency = query.BaseCurrencyCode.Trim().ToUpperInvariant();

        var statusTokens = query.Mode == TaskMarginReportMode.Billed
            ? new[] { TaskStatus.Billed.ToToken() }
            : new[] { TaskStatus.Open.ToToken(), TaskStatus.Completed.ToToken(), TaskStatus.Billed.ToToken() };

        var dateColumn = query.Mode == TaskMarginReportMode.Billed
            ? "t.billed_at::date"
            : "t.service_date";

        var orderColumn = query.Mode == TaskMarginReportMode.Billed
            ? "t.billed_at desc nulls last"
            : "t.service_date desc nulls last";

        // Billable rate resolver. Wrapped as a LATERAL so the
        // billed/unbilled branches each have access to t.*, and so
        // both row + summary queries can share the same expression.
        const string BillableFxJoinClause = """
            left join lateral (
              select
                case
                  when upper(t.currency_code) = @base_currency_code then 1::numeric
                  when t.status = 'billed' and t.billed_invoice_id is not null then
                    (select i.fx_rate
                     from invoices i
                     where i.id = t.billed_invoice_id
                       and i.company_id = t.company_id)
                  else
                    (select fx.rate
                     from fx_rates_daily fx
                     where fx.base_code = upper(t.currency_code)
                       and fx.quote_code = @base_currency_code
                       and fx.rate_date <= current_date
                     order by fx.rate_date desc, fx.fetched_at desc
                     limit 1)
                end as rate,
                case
                  when upper(t.currency_code) = @base_currency_code then true
                  when t.status = 'billed' and t.billed_invoice_id is not null then
                    exists (select 1 from invoices i
                             where i.id = t.billed_invoice_id
                               and i.company_id = t.company_id)
                  else
                    exists (select 1 from fx_rates_daily fx
                             where fx.base_code = upper(t.currency_code)
                               and fx.quote_code = @base_currency_code
                               and fx.rate_date <= current_date)
                end as resolved
            ) billable_fx on true
            """;

        // Cost CTEs return BOTH source-currency sum (amt_native) and
        // base-currency sum (amt_base, each line × its parent doc's
        // fx_rate). The parent fx_rate is the GL-locked translation
        // rate stamped at post time. coalesce defends against rows
        // where fx_rate is null (shouldn't happen for posted docs,
        // but a missing rate falls back to the line's own amount).
        const string BillCostCte = """
            bill_cost as (
              select
                bl.task_id,
                sum(bl.line_amount) as amt_native,
                sum(bl.line_amount * coalesce(b.fx_rate, 1)) as amt_base
              from bill_lines bl
              join bills b on b.id = bl.bill_id
                            and b.company_id = bl.company_id
                            and b.status <> 'voided'
              where bl.company_id = @company_id
                and bl.task_id is not null
              group by bl.task_id
            )
            """;

        const string ExpenseCostCte = """
            expense_cost as (
              -- expense_lines has no company_id of its own; isolate
              -- via the parent expense row.
              select
                el.task_id,
                sum(el.line_total) as amt_native,
                sum(el.line_total * coalesce(e.fx_rate, 1)) as amt_base
              from expense_lines el
              join expenses e on e.id = el.expense_id
                              and e.company_id = @company_id
                              and e.status <> 'voided'
              where el.task_id is not null
              group by el.task_id
            )
            """;

        await using var connection = await connections.OpenAsync(cancellationToken);

        // Row query — paged.
        var rows = new List<TaskMarginRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $$"""
                with
                {{BillCostCte}},
                {{ExpenseCostCte}}
                select
                  t.id,
                  t.task_no,
                  t.title,
                  t.status,
                  t.customer_id,
                  t.assigned_to_user_id,
                  t.service_date,
                  t.billed_at,
                  t.billed_invoice_id,
                  t.currency_code,
                  t.total_billable_value,
                  coalesce(bc.amt_native, 0) + coalesce(ec.amt_native, 0) as direct_cost,
                  coalesce(bc.amt_base, 0)   + coalesce(ec.amt_base, 0)   as direct_cost_base,
                  coalesce(billable_fx.rate, 1)        as billable_fx_rate,
                  coalesce(billable_fx.resolved, false) as billable_fx_resolved
                from tasks t
                left join bill_cost    bc on bc.task_id = t.id
                left join expense_cost ec on ec.task_id = t.id
                {{BillableFxJoinClause}}
                where t.company_id = @company_id
                  and t.status = any(@status_tokens)
                  and (not @has_from or {{dateColumn}} >= @from_date)
                  and (not @has_to   or {{dateColumn}} <= @to_date)
                  and (not @has_customer or t.customer_id = @customer_id)
                  and (not @has_assignee or t.assigned_to_user_id = @assignee_id)
                order by {{orderColumn}}, t.task_no asc
                limit @take offset @skip;
                """;
            BindWhereParameters(command, query, statusTokens, baseCurrency);
            command.Parameters.AddWithValue("take", take);
            command.Parameters.AddWithValue("skip", skip);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var billable = reader.GetDecimal(reader.GetOrdinal("total_billable_value"));
                var directCost = reader.GetDecimal(reader.GetOrdinal("direct_cost"));
                var directCostBase = reader.GetDecimal(reader.GetOrdinal("direct_cost_base"));
                var billableFx = reader.GetDecimal(reader.GetOrdinal("billable_fx_rate"));
                var billableFxResolved = reader.GetBoolean(reader.GetOrdinal("billable_fx_resolved"));
                var billableBase = Math.Round(billable * billableFx, 2, MidpointRounding.ToEven);
                var margin = billable - directCost;
                var marginBase = billableBase - directCostBase;
                rows.Add(new TaskMarginRow
                {
                    TaskId = reader.GetGuid(reader.GetOrdinal("id")),
                    TaskNo = reader.GetString(reader.GetOrdinal("task_no")),
                    Title = reader.GetString(reader.GetOrdinal("title")),
                    Status = TaskStatusExtensions.Parse(reader.GetString(reader.GetOrdinal("status"))),
                    CustomerId = reader.IsDBNull(reader.GetOrdinal("customer_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("customer_id")),
                    AssignedToUserId = reader.IsDBNull(reader.GetOrdinal("assigned_to_user_id"))
                        ? null
                        : UserId.Parse(reader.GetString(reader.GetOrdinal("assigned_to_user_id")).Trim()),
                    ServiceDate = reader.IsDBNull(reader.GetOrdinal("service_date"))
                        ? null
                        : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("service_date")),
                    BilledAtUtc = reader.IsDBNull(reader.GetOrdinal("billed_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("billed_at")),
                    BilledInvoiceId = reader.IsDBNull(reader.GetOrdinal("billed_invoice_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("billed_invoice_id")),
                    CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")),
                    BillableValue = billable,
                    DirectCost = directCost,
                    GrossMargin = margin,
                    GrossMarginPercent = billable == 0m ? null : Math.Round(margin / billable * 100m, 2),
                    BaseCurrencyCode = baseCurrency,
                    FxRate = billableFx,
                    FxResolved = billableFxResolved,
                    BillableValueBase = billableBase,
                    DirectCostBase = directCostBase,
                    GrossMarginBase = marginBase,
                });
            }
        }

        // Summary query — same WHERE + JOINs, no LIMIT, SUMs over the
        // entire filtered set.
        TaskMarginSummary summary;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $$"""
                with
                {{BillCostCte}},
                {{ExpenseCostCte}},
                rows_with_fx as (
                  select
                    t.total_billable_value                                         as billable,
                    coalesce(bc.amt_native, 0) + coalesce(ec.amt_native, 0)        as direct_cost,
                    coalesce(bc.amt_base, 0)   + coalesce(ec.amt_base, 0)          as direct_cost_base,
                    coalesce(billable_fx.rate, 1)                                  as billable_fx_rate,
                    coalesce(billable_fx.resolved, false)                          as billable_fx_resolved
                  from tasks t
                  left join bill_cost    bc on bc.task_id = t.id
                  left join expense_cost ec on ec.task_id = t.id
                  {{BillableFxJoinClause}}
                  where t.company_id = @company_id
                    and t.status = any(@status_tokens)
                    and (not @has_from or {{dateColumn}} >= @from_date)
                    and (not @has_to   or {{dateColumn}} <= @to_date)
                    and (not @has_customer or t.customer_id = @customer_id)
                    and (not @has_assignee or t.assigned_to_user_id = @assignee_id)
                )
                select
                  count(*)::int                                                              as task_count,
                  coalesce(sum(billable), 0)                                                 as billable_sum,
                  coalesce(sum(direct_cost), 0)                                              as cost_sum,
                  coalesce(sum(round(billable * billable_fx_rate, 2)), 0)                    as billable_base_sum,
                  coalesce(sum(direct_cost_base), 0)                                         as cost_base_sum,
                  coalesce(sum(case when not billable_fx_resolved then 1 else 0 end), 0)::int as unresolved_fx_count
                from rows_with_fx;
                """;
            BindWhereParameters(command, query, statusTokens, baseCurrency);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var count = reader.GetInt32(reader.GetOrdinal("task_count"));
            var billableSum = reader.GetDecimal(reader.GetOrdinal("billable_sum"));
            var costSum = reader.GetDecimal(reader.GetOrdinal("cost_sum"));
            var billableBaseSum = reader.GetDecimal(reader.GetOrdinal("billable_base_sum"));
            var costBaseSum = reader.GetDecimal(reader.GetOrdinal("cost_base_sum"));
            var unresolvedCount = reader.GetInt32(reader.GetOrdinal("unresolved_fx_count"));
            var marginSum = billableSum - costSum;
            var marginBaseSum = billableBaseSum - costBaseSum;
            summary = new TaskMarginSummary
            {
                TaskCount = count,
                TotalBillableValue = billableSum,
                TotalDirectCost = costSum,
                TotalGrossMargin = marginSum,
                WeightedGrossMarginPercent = billableSum == 0m
                    ? null
                    : Math.Round(marginSum / billableSum * 100m, 2),
                BaseCurrencyCode = baseCurrency,
                TotalBillableValueBase = billableBaseSum,
                TotalDirectCostBase = costBaseSum,
                TotalGrossMarginBase = marginBaseSum,
                WeightedGrossMarginPercentBase = billableBaseSum == 0m
                    ? null
                    : Math.Round(marginBaseSum / billableBaseSum * 100m, 2),
                UnresolvedFxCount = unresolvedCount,
            };
        }

        return new TaskMarginReportResult
        {
            Mode = query.Mode,
            Rows = rows,
            Summary = summary,
        };
    }

    private static void BindWhereParameters(NpgsqlCommand command, TaskMarginReportQuery query, string[] statusTokens, string baseCurrency)
    {
        command.Parameters.AddWithValue("company_id", query.CompanyId.Value!);
        command.Parameters.AddWithValue("status_tokens", statusTokens);
        command.Parameters.AddWithValue("base_currency_code", baseCurrency);
        command.Parameters.AddWithValue("has_from", query.FromDate.HasValue);
        command.Parameters.AddWithValue(
            "from_date",
            query.FromDate.HasValue ? (object)query.FromDate.Value : DateOnly.MinValue);
        command.Parameters.AddWithValue("has_to", query.ToDate.HasValue);
        command.Parameters.AddWithValue(
            "to_date",
            query.ToDate.HasValue ? (object)query.ToDate.Value : DateOnly.MaxValue);
        command.Parameters.AddWithValue("has_customer", query.CustomerId.HasValue);
        command.Parameters.AddWithValue(
            "customer_id",
            query.CustomerId.HasValue ? (object)query.CustomerId.Value : Guid.Empty);
        command.Parameters.AddWithValue("has_assignee", query.AssignedToUserId.HasValue);
        command.Parameters.AddWithValue(
            "assignee_id",
            query.AssignedToUserId.HasValue ? (object)query.AssignedToUserId.Value.Value! : string.Empty);
    }
}
