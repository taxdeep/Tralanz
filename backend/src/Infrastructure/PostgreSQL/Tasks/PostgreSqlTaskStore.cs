using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Npgsql;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;

namespace Infrastructure.PostgreSQL.Tasks;

public sealed class PostgreSqlTaskStore(PostgreSqlConnectionFactory connections) : ITaskStore
{
    private const string SchemaSql =
        """
        create table if not exists tasks (
          id                    uuid          primary key default gen_random_uuid(),
          company_id            char(7)       not null,
          task_no               varchar(32)   not null,
          title                 varchar(200)  not null,
          description           text          null,
          customer_id           uuid          null,
          project_id            uuid          null,
          assigned_to_user_id   char(7)       null,
          status                varchar(16)   not null,
          service_date          date          null,
          ready_to_bill_at      timestamptz   null,
          billed_invoice_id     uuid          null,
          billed_at             timestamptz   null,
          total_billable_value  numeric(20, 4) not null default 0,
          currency_code         char(3)       not null,
          is_voided             boolean       not null default false,
          created_at            timestamptz   not null default now(),
          created_by            char(7)       not null,
          updated_at            timestamptz   not null default now(),
          constraint uq_tasks_company_task_no unique (company_id, task_no)
        );

        create index if not exists ix_tasks_company_status on tasks (company_id, status);
        create index if not exists ix_tasks_company_customer on tasks (company_id, customer_id);
        create index if not exists ix_tasks_company_assignee
          on tasks (company_id, assigned_to_user_id)
          where assigned_to_user_id is not null;

        create table if not exists task_lines (
          id                    uuid          primary key default gen_random_uuid(),
          company_id            char(7)       not null,
          task_id               uuid          not null references tasks(id) on delete cascade,
          line_no               int           not null,
          item_id               uuid          not null,
          description           varchar(400)  null,
          quantity              numeric(20, 4) not null,
          unit_price            numeric(20, 4) not null,
          currency_code         char(3)       not null,
          line_amount           numeric(20, 4) not null,
          tax_code_id           uuid          null,
          constraint uq_task_lines_task_line_no unique (company_id, task_id, line_no)
        );
        create index if not exists ix_task_lines_company_task on task_lines (company_id, task_id, line_no);

        create table if not exists task_state_transitions (
          id                bigserial primary key,
          company_id        char(7)       not null,
          task_id           uuid          not null,
          from_status       varchar(16)   not null,
          to_status         varchar(16)   not null,
          reason            varchar(200)  null,
          actor_user_id     char(7)       not null,
          occurred_at       timestamptz   not null default now()
        );
        create index if not exists ix_task_state_transitions_task
          on task_state_transitions (company_id, task_id, occurred_at desc);

        -- Per-company task number sequence. Singleton row per company;
        -- next_ordinal increments atomically inside the create flow.
        create table if not exists tasks_company_sequence (
          company_id    char(7) primary key,
          next_ordinal  bigint  not null default 1
        );
        """;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TaskRecord?> GetAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var header = await ReadHeaderAsync(connection, transaction: null, companyId, taskId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await ReadLinesAsync(connection, transaction: null, companyId, taskId, cancellationToken);
        return ToRecord(header, lines);
    }

    public async Task<IReadOnlyList<TaskSummary>> ListAsync(
        TaskQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, company_id, task_no, title, customer_id, assigned_to_user_id,
                   status, service_date, total_billable_value, currency_code, updated_at
            from tasks
            where company_id = @company_id
              and (@status is null or status = @status)
              and (@customer_id is null or customer_id = @customer_id)
              and (@assignee_filter is null or assigned_to_user_id = @assignee_filter)
              and is_voided = false
            order by
              -- Open first, then completed, then everything else, with
              -- the most-recently-touched on top inside each bucket.
              case status
                when 'open' then 0
                when 'completed' then 1
                else 2
              end,
              updated_at desc
            offset @skip
            limit @take;
            """;
        command.Parameters.AddWithValue("company_id", query.CompanyId);
        command.Parameters.AddWithValue(
            "status",
            query.Status.HasValue ? (object)query.Status.Value.ToToken() : DBNull.Value);
        command.Parameters.AddWithValue(
            "customer_id",
            query.CustomerId.HasValue ? (object)query.CustomerId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "assignee_filter",
            query.OnlyAssignedToUserId.HasValue ? (object)query.OnlyAssignedToUserId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("skip", Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("take", Math.Clamp(query.Take, 1, 200));

        var rows = new List<TaskSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TaskSummary
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                TaskNo = reader.GetString(reader.GetOrdinal("task_no")).Trim(),
                Title = reader.GetString(reader.GetOrdinal("title")),
                CustomerId = reader.IsDBNull(reader.GetOrdinal("customer_id")) ? null : reader.GetGuid(reader.GetOrdinal("customer_id")),
                AssignedToUserId = reader.IsDBNull(reader.GetOrdinal("assigned_to_user_id"))
                    ? null
                    : UserId.Parse(reader.GetString(reader.GetOrdinal("assigned_to_user_id")).Trim()),
                Status = TaskStatusExtensions.Parse(reader.GetString(reader.GetOrdinal("status"))),
                ServiceDate = reader.IsDBNull(reader.GetOrdinal("service_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("service_date")),
                TotalBillableValue = reader.GetDecimal(reader.GetOrdinal("total_billable_value")),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")).Trim().ToUpperInvariant(),
                UpdatedAtUtc = ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at"))),
            });
        }

        return rows;
    }

    public async Task<TaskRecord> CreateAsync(
        CompanyId companyId,
        UserId createdBy,
        string title,
        string? description,
        Guid? customerId,
        Guid? projectId,
        UserId? assignedToUserId,
        DateOnly? serviceDate,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ordinal = await AllocateTaskOrdinalAsync(connection, transaction, companyId, cancellationToken);
        var taskNo = $"TSK-{ordinal:D6}";

        var newId = Guid.NewGuid();
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into tasks (
                  id, company_id, task_no, title, description, customer_id, project_id,
                  assigned_to_user_id, status, service_date, currency_code,
                  total_billable_value, is_voided,
                  created_at, created_by, updated_at
                )
                values (
                  @id, @company_id, @task_no, @title, @description, @customer_id, @project_id,
                  @assigned_to, 'open', @service_date, @currency_code,
                  0, false,
                  now(), @created_by, now()
                );
                """;
            insertCommand.Parameters.AddWithValue("id", newId);
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("task_no", taskNo);
            insertCommand.Parameters.AddWithValue("title", title);
            insertCommand.Parameters.AddWithValue("description", description is null ? DBNull.Value : (object)description);
            insertCommand.Parameters.AddWithValue("customer_id", customerId.HasValue ? (object)customerId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue("project_id", projectId.HasValue ? (object)projectId.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "assigned_to",
                assignedToUserId.HasValue ? (object)assignedToUserId.Value.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue("service_date", serviceDate.HasValue ? (object)serviceDate.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue("currency_code", currencyCode);
            insertCommand.Parameters.AddWithValue("created_by", createdBy);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(companyId, newId, cancellationToken)
            ?? throw new InvalidOperationException($"Task '{newId:D}' was not visible immediately after creation.");
    }

    public async Task<TaskRecord?> UpdateHeaderAsync(
        CompanyId companyId,
        Guid taskId,
        string title,
        string? description,
        Guid? customerId,
        Guid? projectId,
        UserId? assignedToUserId,
        DateOnly? serviceDate,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update tasks
            set title = @title,
                description = @description,
                customer_id = @customer_id,
                project_id = @project_id,
                assigned_to_user_id = @assigned_to,
                service_date = @service_date,
                updated_at = now()
            where company_id = @company_id
              and id = @id
              and status = 'open';
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", taskId);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("description", description is null ? DBNull.Value : (object)description);
        command.Parameters.AddWithValue("customer_id", customerId.HasValue ? (object)customerId.Value : DBNull.Value);
        command.Parameters.AddWithValue("project_id", projectId.HasValue ? (object)projectId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "assigned_to",
            assignedToUserId.HasValue ? (object)assignedToUserId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("service_date", serviceDate.HasValue ? (object)serviceDate.Value : DBNull.Value);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return null;
        }

        return await GetAsync(companyId, taskId, cancellationToken);
    }

    public async Task<TaskRecord> AppendLineAsync(
        CompanyId companyId,
        Guid taskId,
        Guid itemId,
        string? description,
        decimal quantity,
        decimal unitPrice,
        string currencyCode,
        Guid? taxCodeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Lock the task header so concurrent AppendLine calls
        // serialize on line_no allocation + total recompute.
        var header = await ReadHeaderForUpdateAsync(connection, transaction, companyId, taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task '{taskId:D}' was not found.");

        if (!string.Equals(header.Status, "open", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Task is in status '{header.Status}' and is no longer editable.");
        }

        var lineAmount = decimal.Round(quantity * unitPrice, 4, MidpointRounding.ToEven);

        int nextLineNo;
        await using (var lineNoCommand = connection.CreateCommand())
        {
            lineNoCommand.Transaction = transaction;
            lineNoCommand.CommandText =
                """
                select coalesce(max(line_no), 0) + 1
                from task_lines
                where company_id = @company_id and task_id = @task_id;
                """;
            lineNoCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineNoCommand.Parameters.AddWithValue("task_id", taskId);
            nextLineNo = Convert.ToInt32(await lineNoCommand.ExecuteScalarAsync(cancellationToken) ?? 1);
        }

        await using (var insertLine = connection.CreateCommand())
        {
            insertLine.Transaction = transaction;
            insertLine.CommandText =
                """
                insert into task_lines (
                  company_id, task_id, line_no, item_id, description,
                  quantity, unit_price, currency_code, line_amount, tax_code_id
                )
                values (
                  @company_id, @task_id, @line_no, @item_id, @description,
                  @quantity, @unit_price, @currency_code, @line_amount, @tax_code_id
                );
                """;
            insertLine.Parameters.AddWithValue("company_id", companyId.Value);
            insertLine.Parameters.AddWithValue("task_id", taskId);
            insertLine.Parameters.AddWithValue("line_no", nextLineNo);
            insertLine.Parameters.AddWithValue("item_id", itemId);
            insertLine.Parameters.AddWithValue("description", description is null ? DBNull.Value : (object)description);
            insertLine.Parameters.AddWithValue("quantity", quantity);
            insertLine.Parameters.AddWithValue("unit_price", unitPrice);
            insertLine.Parameters.AddWithValue("currency_code", currencyCode);
            insertLine.Parameters.AddWithValue("line_amount", lineAmount);
            insertLine.Parameters.AddWithValue("tax_code_id", taxCodeId.HasValue ? (object)taxCodeId.Value : DBNull.Value);
            await insertLine.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecomputeTotalsAsync(connection, transaction, companyId, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(companyId, taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task '{taskId:D}' disappeared after appending a line.");
    }

    public async Task<TaskRecord> RemoveLineAsync(
        CompanyId companyId,
        Guid taskId,
        Guid lineId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var header = await ReadHeaderForUpdateAsync(connection, transaction, companyId, taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task '{taskId:D}' was not found.");

        if (!string.Equals(header.Status, "open", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Task is in status '{header.Status}' and is no longer editable.");
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from task_lines
                where company_id = @company_id
                  and task_id = @task_id
                  and id = @line_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("task_id", taskId);
            deleteCommand.Parameters.AddWithValue("line_id", lineId);
            var affected = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException($"Task line '{lineId:D}' was not found.");
            }
        }

        await RecomputeTotalsAsync(connection, transaction, companyId, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(companyId, taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task '{taskId:D}' disappeared after removing a line.");
    }

    public async Task<TaskRecord?> TransitionStatusAsync(
        CompanyId companyId,
        Guid taskId,
        TaskStatus fromStatus,
        TaskStatus toStatus,
        UserId actorUserId,
        string? reason,
        Guid? billedInvoiceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var fromToken = fromStatus.ToToken();
        var toToken = toStatus.ToToken();

        int affected;
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update tasks
                set status = @to_status,
                    ready_to_bill_at = case
                      when @to_status = 'completed' and ready_to_bill_at is null then now()
                      else ready_to_bill_at
                    end,
                    billed_invoice_id = case
                      when @to_status = 'billed' then @billed_invoice_id
                      when @to_status = 'completed' then null
                      else billed_invoice_id
                    end,
                    billed_at = case
                      when @to_status = 'billed' then now()
                      when @to_status = 'completed' then null
                      else billed_at
                    end,
                    updated_at = now()
                where company_id = @company_id
                  and id = @id
                  and status = @from_status;
                """;
            updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateCommand.Parameters.AddWithValue("id", taskId);
            updateCommand.Parameters.AddWithValue("from_status", fromToken);
            updateCommand.Parameters.AddWithValue("to_status", toToken);
            updateCommand.Parameters.AddWithValue(
                "billed_invoice_id",
                billedInvoiceId.HasValue ? (object)billedInvoiceId.Value : DBNull.Value);

            affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var auditCommand = connection.CreateCommand())
        {
            auditCommand.Transaction = transaction;
            auditCommand.CommandText =
                """
                insert into task_state_transitions (
                  company_id, task_id, from_status, to_status, reason, actor_user_id
                )
                values (
                  @company_id, @task_id, @from_status, @to_status, @reason, @actor_user_id
                );
                """;
            auditCommand.Parameters.AddWithValue("company_id", companyId.Value);
            auditCommand.Parameters.AddWithValue("task_id", taskId);
            auditCommand.Parameters.AddWithValue("from_status", fromToken);
            auditCommand.Parameters.AddWithValue("to_status", toToken);
            auditCommand.Parameters.AddWithValue("reason", reason is null ? DBNull.Value : (object)reason);
            auditCommand.Parameters.AddWithValue("actor_user_id", actorUserId);
            await auditCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(companyId, taskId, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskSummary>> ListByBilledInvoiceAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, company_id, task_no, title, customer_id, assigned_to_user_id,
                   status, service_date, total_billable_value, currency_code, updated_at
            from tasks
            where company_id = @company_id
              and billed_invoice_id = @invoice_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_id", invoiceId);

        var rows = new List<TaskSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TaskSummary
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                TaskNo = reader.GetString(reader.GetOrdinal("task_no")).Trim(),
                Title = reader.GetString(reader.GetOrdinal("title")),
                CustomerId = reader.IsDBNull(reader.GetOrdinal("customer_id")) ? null : reader.GetGuid(reader.GetOrdinal("customer_id")),
                AssignedToUserId = reader.IsDBNull(reader.GetOrdinal("assigned_to_user_id"))
                    ? null
                    : UserId.Parse(reader.GetString(reader.GetOrdinal("assigned_to_user_id")).Trim()),
                Status = TaskStatusExtensions.Parse(reader.GetString(reader.GetOrdinal("status"))),
                ServiceDate = reader.IsDBNull(reader.GetOrdinal("service_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("service_date")),
                TotalBillableValue = reader.GetDecimal(reader.GetOrdinal("total_billable_value")),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")).Trim().ToUpperInvariant(),
                UpdatedAtUtc = ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at"))),
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<TaskDisplayLookup>> LookupDisplayAsync(
        CompanyId companyId,
        IReadOnlyList<Guid> taskIds,
        CancellationToken cancellationToken)
    {
        if (taskIds is null || taskIds.Count == 0)
        {
            return Array.Empty<TaskDisplayLookup>();
        }

        // Distinct + non-empty filter: callers (Bill / CreditMemo edit
        // pages) may pass the raw per-line array which can contain
        // duplicates or empties.
        var ids = taskIds
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return Array.Empty<TaskDisplayLookup>();
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, task_no, title
            from tasks
            where company_id = @company_id
              and id = any(@ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("ids", ids);

        var rows = new List<TaskDisplayLookup>(ids.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TaskDisplayLookup(
                TaskId: reader.GetGuid(0),
                TaskNo: reader.GetString(1).Trim(),
                Title: reader.GetString(2)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<TaskStateTransitionRecord>> ListTransitionsAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, company_id, task_id, from_status, to_status, reason, actor_user_id, occurred_at
            from task_state_transitions
            where company_id = @company_id and task_id = @task_id
            order by occurred_at asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("task_id", taskId);

        var rows = new List<TaskStateTransitionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TaskStateTransitionRecord(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                TaskId: reader.GetGuid(reader.GetOrdinal("task_id")),
                FromStatus: TaskStatusExtensions.Parse(reader.GetString(reader.GetOrdinal("from_status"))),
                ToStatus: TaskStatusExtensions.Parse(reader.GetString(reader.GetOrdinal("to_status"))),
                Reason: reader.IsDBNull(reader.GetOrdinal("reason")) ? null : reader.GetString(reader.GetOrdinal("reason")),
                ActorUserId: UserId.Parse(reader.GetString(reader.GetOrdinal("actor_user_id"))),
                OccurredAtUtc: ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("occurred_at")))));
        }

        return rows;
    }

    private static async Task<long> AllocateTaskOrdinalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        // Upsert + increment in a single statement. Returns the
        // ordinal the caller should use. The on-conflict branch
        // returns the post-increment value, so we subtract 1 to get
        // the just-allocated ordinal; the insert branch returns the
        // seed value (1) which is exactly the allocation.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into tasks_company_sequence (company_id, next_ordinal)
            values (@company_id, 2)
            on conflict (company_id) do update
              set next_ordinal = tasks_company_sequence.next_ordinal + 1
            returning case
              when tasks_company_sequence.next_ordinal = 2
                then 1
              else tasks_company_sequence.next_ordinal - 1
            end as allocated;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(raw ?? 1L);
    }

    private static async Task RecomputeTotalsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update tasks
            set total_billable_value = coalesce((
                  select sum(line_amount)
                  from task_lines
                  where company_id = @company_id and task_id = @task_id
                ), 0),
                updated_at = now()
            where company_id = @company_id and id = @task_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("task_id", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TaskHeaderRow?> ReadHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TaskHeaderSelect + " where company_id = @company_id and id = @id limit 1;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", taskId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadHeader(reader) : null;
    }

    private static async Task<TaskHeaderRow?> ReadHeaderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TaskHeaderSelect + " where company_id = @company_id and id = @id for update;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", taskId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadHeader(reader) : null;
    }

    private static async Task<IReadOnlyList<TaskLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, company_id, task_id, line_no, item_id, description,
                   quantity, unit_price, currency_code, line_amount, tax_code_id
            from task_lines
            where company_id = @company_id and task_id = @task_id
            order by line_no asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("task_id", taskId);

        var lines = new List<TaskLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new TaskLineRecord(
                Id: reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                TaskId: reader.GetGuid(reader.GetOrdinal("task_id")),
                LineNo: reader.GetInt32(reader.GetOrdinal("line_no")),
                ItemId: reader.GetGuid(reader.GetOrdinal("item_id")),
                Description: reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                Quantity: reader.GetDecimal(reader.GetOrdinal("quantity")),
                UnitPrice: reader.GetDecimal(reader.GetOrdinal("unit_price")),
                CurrencyCode: reader.GetString(reader.GetOrdinal("currency_code")).Trim().ToUpperInvariant(),
                LineAmount: reader.GetDecimal(reader.GetOrdinal("line_amount")),
                TaxCodeId: reader.IsDBNull(reader.GetOrdinal("tax_code_id")) ? null : reader.GetGuid(reader.GetOrdinal("tax_code_id"))));
        }

        return lines;
    }

    private const string TaskHeaderSelect =
        """
        select id, company_id, task_no, title, description, customer_id, project_id,
               assigned_to_user_id, status, service_date, ready_to_bill_at,
               billed_invoice_id, billed_at, total_billable_value,
               currency_code, is_voided, created_at, created_by, updated_at
        from tasks
        """;

    private static TaskHeaderRow ReadHeader(NpgsqlDataReader reader) =>
        new(
            Id: reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            TaskNo: reader.GetString(reader.GetOrdinal("task_no")).Trim(),
            Title: reader.GetString(reader.GetOrdinal("title")),
            Description: reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            CustomerId: reader.IsDBNull(reader.GetOrdinal("customer_id")) ? null : reader.GetGuid(reader.GetOrdinal("customer_id")),
            ProjectId: reader.IsDBNull(reader.GetOrdinal("project_id")) ? null : reader.GetGuid(reader.GetOrdinal("project_id")),
            AssignedToUserId: reader.IsDBNull(reader.GetOrdinal("assigned_to_user_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("assigned_to_user_id")).Trim()),
            Status: reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
            ServiceDate: reader.IsDBNull(reader.GetOrdinal("service_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("service_date")),
            ReadyToBillAtUtc: reader.IsDBNull(reader.GetOrdinal("ready_to_bill_at"))
                ? null
                : ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("ready_to_bill_at"))),
            BilledInvoiceId: reader.IsDBNull(reader.GetOrdinal("billed_invoice_id")) ? null : reader.GetGuid(reader.GetOrdinal("billed_invoice_id")),
            BilledAtUtc: reader.IsDBNull(reader.GetOrdinal("billed_at"))
                ? null
                : ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("billed_at"))),
            TotalBillableValue: reader.GetDecimal(reader.GetOrdinal("total_billable_value")),
            CurrencyCode: reader.GetString(reader.GetOrdinal("currency_code")).Trim().ToUpperInvariant(),
            IsVoided: reader.GetBoolean(reader.GetOrdinal("is_voided")),
            CreatedAtUtc: ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("created_at"))),
            CreatedBy: UserId.Parse(reader.GetString(reader.GetOrdinal("created_by")).Trim()),
            UpdatedAtUtc: ToUtc(reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at"))));

    private static TaskRecord ToRecord(TaskHeaderRow h, IReadOnlyList<TaskLineRecord> lines) =>
        new()
        {
            Id = h.Id,
            CompanyId = h.CompanyId,
            TaskNo = h.TaskNo,
            Title = h.Title,
            Description = h.Description,
            CustomerId = h.CustomerId,
            ProjectId = h.ProjectId,
            AssignedToUserId = h.AssignedToUserId,
            Status = TaskStatusExtensions.Parse(h.Status),
            ServiceDate = h.ServiceDate,
            ReadyToBillAtUtc = h.ReadyToBillAtUtc,
            BilledInvoiceId = h.BilledInvoiceId,
            BilledAtUtc = h.BilledAtUtc,
            TotalBillableValue = h.TotalBillableValue,
            CurrencyCode = h.CurrencyCode,
            IsVoided = h.IsVoided,
            CreatedAtUtc = h.CreatedAtUtc,
            CreatedBy = h.CreatedBy,
            UpdatedAtUtc = h.UpdatedAtUtc,
            Lines = lines,
        };

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private sealed record TaskHeaderRow(
        Guid Id,
        CompanyId CompanyId,
        string TaskNo,
        string Title,
        string? Description,
        Guid? CustomerId,
        Guid? ProjectId,
        UserId? AssignedToUserId,
        string Status,
        DateOnly? ServiceDate,
        DateTimeOffset? ReadyToBillAtUtc,
        Guid? BilledInvoiceId,
        DateTimeOffset? BilledAtUtc,
        decimal TotalBillableValue,
        string CurrencyCode,
        bool IsVoided,
        DateTimeOffset CreatedAtUtc,
        UserId CreatedBy,
        DateTimeOffset UpdatedAtUtc);
}
