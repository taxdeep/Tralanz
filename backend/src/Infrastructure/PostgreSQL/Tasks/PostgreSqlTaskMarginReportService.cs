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
/// cheap as long as task attribution stays sparse (a few percent of
/// total lines). When the report starts to dominate the cost report
/// page latency, layer a refresh-on-write cache on top — leave this
/// service intact as the source of truth.
///
/// FX conversion: each row joins to fx_rates_daily using the task's
/// natural "as-of" date (service_date for operational mode, billed_at
/// for billed mode) to translate amounts into the company base
/// currency. When the source currency already matches the base, or
/// when no rate row exists, the conversion falls back to rate = 1
/// and FxResolved = false so the UI can badge the row. Per-cost-line
/// dates would give a more honest cost conversion (each bill / expense
/// at its own posting date) — current implementation uses the task's
/// date for the whole row, accepted as a v1 simplification.
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

        // Operational mode covers every non-cancelled task; billed
        // mode covers billed-only. The date-filter column also shifts
        // (service_date for in-flight context, billed_at for realised
        // context). The status filter is a Postgres text[] passed via
        // ANY(). dateColumn is also the as-of date for FX resolution.
        var statusTokens = query.Mode == TaskMarginReportMode.Billed
            ? new[] { TaskStatus.Billed.ToToken() }
            : new[] { TaskStatus.Open.ToToken(), TaskStatus.Completed.ToToken(), TaskStatus.Billed.ToToken() };

        var dateColumn = query.Mode == TaskMarginReportMode.Billed
            ? "t.billed_at::date"
            : "t.service_date";

        var orderColumn = query.Mode == TaskMarginReportMode.Billed
            ? "t.billed_at desc nulls last"
            : "t.service_date desc nulls last";

        // FX lookup applied to every row. LATERAL so the rate row is
        // chosen per-task based on its own as-of date; LEFT JOIN so a
        // missing rate doesn't drop the row. coalesce(dateColumn,
        // current_date) handles tasks with no service/bill date by
        // falling back to today's rate.
        const string FxJoinClause = """
            left join lateral (
              select fx.rate
              from fx_rates_daily fx
              where fx.base_code = upper(t.currency_code)
                and fx.quote_code = @base_currency_code
                and fx.rate_date <= coalesce({{dateColumn}}, current_date)
              order by fx.rate_date desc, fx.fetched_at desc
              limit 1
            ) fx_rate on true
            """;

        // Per-row computed FX rate + base-currency amounts. When
        // currency_code already equals the base, no JOIN needed →
        // rate=1, resolved=true. Otherwise use the JOIN result, with
        // 1+false as fallback when no row was found.
        const string FxProjection = """
            case
              when upper(t.currency_code) = @base_currency_code then 1::numeric
              when fx_rate.rate is not null then fx_rate.rate
              else 1::numeric
            end as fx_rate_resolved,
            case
              when upper(t.currency_code) = @base_currency_code then true
              when fx_rate.rate is not null then true
              else false
            end as fx_resolved
            """;

        await using var connection = await connections.OpenAsync(cancellationToken);

        // Row query — paged. Includes per-row FX rate + base amounts.
        var rows = new List<TaskMarginRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $$"""
                with bill_cost as (
                  select bl.task_id, sum(bl.line_amount) as amt
                  from bill_lines bl
                  join bills b on b.id = bl.bill_id
                                and b.company_id = bl.company_id
                                and b.status <> 'voided'
                  where bl.company_id = @company_id
                    and bl.task_id is not null
                  group by bl.task_id
                ),
                expense_cost as (
                  -- expense_lines has no company_id of its own; isolate
                  -- via the parent expense row.
                  select el.task_id, sum(el.line_total) as amt
                  from expense_lines el
                  join expenses e on e.id = el.expense_id
                                  and e.company_id = @company_id
                                  and e.status <> 'voided'
                  where el.task_id is not null
                  group by el.task_id
                )
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
                  coalesce(bc.amt, 0) + coalesce(ec.amt, 0) as direct_cost,
                  {{FxProjection}}
                from tasks t
                left join bill_cost    bc on bc.task_id = t.id
                left join expense_cost ec on ec.task_id = t.id
                {{FxJoinClause.Replace("{{dateColumn}}", dateColumn)}}
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
                var margin = billable - directCost;
                var fxRate = reader.GetDecimal(reader.GetOrdinal("fx_rate_resolved"));
                var fxResolved = reader.GetBoolean(reader.GetOrdinal("fx_resolved"));
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
                    FxRate = fxRate,
                    FxResolved = fxResolved,
                    BillableValueBase = Math.Round(billable * fxRate, 2, MidpointRounding.ToEven),
                    DirectCostBase = Math.Round(directCost * fxRate, 2, MidpointRounding.ToEven),
                    GrossMarginBase = Math.Round(margin * fxRate, 2, MidpointRounding.ToEven),
                });
            }
        }

        // Summary query — same WHERE + FX JOIN, no LIMIT, returns SUMs
        // over the entire filtered set (not just the visible page).
        TaskMarginSummary summary;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $$"""
                with bill_cost as (
                  select bl.task_id, sum(bl.line_amount) as amt
                  from bill_lines bl
                  join bills b on b.id = bl.bill_id
                                and b.company_id = bl.company_id
                                and b.status <> 'voided'
                  where bl.company_id = @company_id
                    and bl.task_id is not null
                  group by bl.task_id
                ),
                expense_cost as (
                  select el.task_id, sum(el.line_total) as amt
                  from expense_lines el
                  join expenses e on e.id = el.expense_id
                                  and e.company_id = @company_id
                                  and e.status <> 'voided'
                  where el.task_id is not null
                  group by el.task_id
                ),
                rows_with_fx as (
                  select
                    t.total_billable_value                                         as billable,
                    coalesce(bc.amt, 0) + coalesce(ec.amt, 0)                      as direct_cost,
                    {{FxProjection}}
                  from tasks t
                  left join bill_cost    bc on bc.task_id = t.id
                  left join expense_cost ec on ec.task_id = t.id
                  {{FxJoinClause.Replace("{{dateColumn}}", dateColumn)}}
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
                  coalesce(sum(round(billable * fx_rate_resolved, 2)), 0)                    as billable_base_sum,
                  coalesce(sum(round(direct_cost * fx_rate_resolved, 2)), 0)                 as cost_base_sum,
                  coalesce(sum(case when not fx_resolved then 1 else 0 end), 0)::int         as unresolved_fx_count
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
