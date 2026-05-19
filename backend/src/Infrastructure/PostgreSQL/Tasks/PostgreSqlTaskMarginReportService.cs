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

        var take = Math.Clamp(query.Take, 1, 500);
        var skip = Math.Max(0, query.Skip);

        // Operational mode covers every non-cancelled task; billed
        // mode covers billed-only. The date-filter column also shifts
        // (service_date for in-flight context, billed_at for realised
        // context). The status filter is a Postgres text[] passed via
        // ANY().
        var statusTokens = query.Mode == TaskMarginReportMode.Billed
            ? new[] { TaskStatus.Billed.ToToken() }
            : new[] { TaskStatus.Open.ToToken(), TaskStatus.Completed.ToToken(), TaskStatus.Billed.ToToken() };

        var dateColumn = query.Mode == TaskMarginReportMode.Billed
            ? "t.billed_at::date"
            : "t.service_date";

        var orderColumn = query.Mode == TaskMarginReportMode.Billed
            ? "t.billed_at desc nulls last"
            : "t.service_date desc nulls last";

        await using var connection = await connections.OpenAsync(cancellationToken);

        // Row query — paged.
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
                  coalesce(bc.amt, 0) + coalesce(ec.amt, 0) as direct_cost
                from tasks t
                left join bill_cost    bc on bc.task_id = t.id
                left join expense_cost ec on ec.task_id = t.id
                where t.company_id = @company_id
                  and t.status = any(@status_tokens)
                  and (not @has_from or {{dateColumn}} >= @from_date)
                  and (not @has_to   or {{dateColumn}} <= @to_date)
                  and (not @has_customer or t.customer_id = @customer_id)
                  and (not @has_assignee or t.assigned_to_user_id = @assignee_id)
                order by {{orderColumn}}, t.task_no asc
                limit @take offset @skip;
                """;
            BindWhereParameters(command, query, statusTokens);
            command.Parameters.AddWithValue("take", take);
            command.Parameters.AddWithValue("skip", skip);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var billable = reader.GetDecimal(reader.GetOrdinal("total_billable_value"));
                var directCost = reader.GetDecimal(reader.GetOrdinal("direct_cost"));
                var margin = billable - directCost;
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
                });
            }
        }

        // Summary query — same WHERE, no LIMIT, returns SUMs over the
        // entire filtered set (not just the visible page).
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
                )
                select
                  count(*)::int                                                  as task_count,
                  coalesce(sum(t.total_billable_value), 0)                       as billable_sum,
                  coalesce(sum(coalesce(bc.amt, 0) + coalesce(ec.amt, 0)), 0)    as cost_sum
                from tasks t
                left join bill_cost    bc on bc.task_id = t.id
                left join expense_cost ec on ec.task_id = t.id
                where t.company_id = @company_id
                  and t.status = any(@status_tokens)
                  and (not @has_from or {{dateColumn}} >= @from_date)
                  and (not @has_to   or {{dateColumn}} <= @to_date)
                  and (not @has_customer or t.customer_id = @customer_id)
                  and (not @has_assignee or t.assigned_to_user_id = @assignee_id);
                """;
            BindWhereParameters(command, query, statusTokens);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var count = reader.GetInt32(reader.GetOrdinal("task_count"));
            var billableSum = reader.GetDecimal(reader.GetOrdinal("billable_sum"));
            var costSum = reader.GetDecimal(reader.GetOrdinal("cost_sum"));
            var marginSum = billableSum - costSum;
            summary = new TaskMarginSummary
            {
                TaskCount = count,
                TotalBillableValue = billableSum,
                TotalDirectCost = costSum,
                TotalGrossMargin = marginSum,
                WeightedGrossMarginPercent = billableSum == 0m
                    ? null
                    : Math.Round(marginSum / billableSum * 100m, 2),
            };
        }

        return new TaskMarginReportResult
        {
            Mode = query.Mode,
            Rows = rows,
            Summary = summary,
        };
    }

    private static void BindWhereParameters(NpgsqlCommand command, TaskMarginReportQuery query, string[] statusTokens)
    {
        command.Parameters.AddWithValue("company_id", query.CompanyId.Value!);
        command.Parameters.AddWithValue("status_tokens", statusTokens);
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
