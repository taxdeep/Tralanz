using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Periods;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M7 iter 1 implementation. Owns accounting-period reads,
/// transitions, and the lazy-seed of one fiscal year of monthly
/// periods on first read for a company that has no periods yet.
/// </summary>
public sealed class PostgresAccountingPeriodRepository : IAccountingPeriodRepository
{
    private const string EntityType = "accounting_period";
    private const string TransitionAction = "accounting_period_state_changed";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;
    private int _schemaEnsured;

    public PostgresAccountingPeriodRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<AccountingPeriod>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        var periods = await ReadAllAsync(scope, companyId, cancellationToken);
        if (periods.Count > 0)
        {
            return periods;
        }

        // First call for this company — seed the current fiscal year.
        // Done inside the same scope so concurrent first-readers don't
        // both seed (the unique constraint on (company_id, period_start,
        // period_end) catches a race; one wins, the other gets back
        // the loser's empty list and re-reads).
        var seeded = await SeedCurrentFiscalYearAsync(scope, companyId, cancellationToken);
        return seeded;
    }

    public async Task<AccountingPeriod> TransitionAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid periodId,
        string targetStatus,
        CancellationToken cancellationToken)
    {
        if (!AccountingPeriodStatus.All.Contains(targetStatus))
        {
            throw new InvalidOperationException(
                $"Unknown target status '{targetStatus}'. Allowed: {string.Join(", ", AccountingPeriodStatus.All)}.");
        }

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Lock the row so a concurrent transition can't double-flip it.
        AccountingPeriod current;
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                select
                  id, company_id, period_start, period_end, status,
                  closing_started_at, closed_at, locked_at,
                  created_at, updated_at
                from accounting_periods
                where company_id = @company_id and id = @period_id
                for update;
                """;
            readCommand.Parameters.AddWithValue("company_id", companyId.Value);
            readCommand.Parameters.AddWithValue("period_id", periodId);

            await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Accounting period {periodId:D} was not found for the active company.");
            }
            current = ReadPeriod(reader);
        }

        if (string.Equals(current.Status, targetStatus, StringComparison.Ordinal))
        {
            // No-op transition — return current without writing.
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        if (!AccountingPeriodStatus.IsAllowedTransition(current.Status, targetStatus))
        {
            throw new InvalidOperationException(
                $"Period transition '{current.Status}' → '{targetStatus}' is not allowed. " +
                "V1 forward-only path is open → closing → closed → locked.");
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? closingAt = current.ClosingStartedAt;
        DateTimeOffset? closedAt = current.ClosedAt;
        DateTimeOffset? lockedAt = current.LockedAt;
        if (targetStatus == AccountingPeriodStatus.Closing) closingAt = now;
        if (targetStatus == AccountingPeriodStatus.Closed) closedAt = now;
        if (targetStatus == AccountingPeriodStatus.Locked) lockedAt = now;

        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                update accounting_periods
                set status = @status,
                    closing_started_at = @closing_started_at,
                    closed_at = @closed_at,
                    locked_at = @locked_at,
                    updated_at = now()
                where company_id = @company_id and id = @period_id;
                """;
            updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateCommand.Parameters.AddWithValue("period_id", periodId);
            updateCommand.Parameters.AddWithValue("status", targetStatus);
            updateCommand.Parameters.Add(new NpgsqlParameter("closing_started_at", NpgsqlDbType.TimestampTz)
            {
                Value = (object?)closingAt ?? DBNull.Value,
            });
            updateCommand.Parameters.Add(new NpgsqlParameter("closed_at", NpgsqlDbType.TimestampTz)
            {
                Value = (object?)closedAt ?? DBNull.Value,
            });
            updateCommand.Parameters.Add(new NpgsqlParameter("locked_at", NpgsqlDbType.TimestampTz)
            {
                Value = (object?)lockedAt ?? DBNull.Value,
            });
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Audit log — append-only, JSON payload carries the full
        // transition context for later forensics.
        await using (var auditCommand = connection.CreateCommand())
        {
            auditCommand.Transaction = transaction;
            auditCommand.CommandText = """
                insert into audit_logs (company_id, actor_type, actor_id, entity_type, entity_id, action, payload)
                values (@company_id, 'user', @actor_id, @entity_type, @period_id, @action,
                  jsonb_build_object(
                    'period_start', @period_start::text,
                    'period_end', @period_end::text,
                    'old_status', @old_status,
                    'new_status', @new_status));
                """;
            auditCommand.Parameters.AddWithValue("company_id", companyId.Value);
            auditCommand.Parameters.AddWithValue("actor_id", actorUserId.Value);
            auditCommand.Parameters.AddWithValue("entity_type", EntityType);
            auditCommand.Parameters.AddWithValue("period_id", periodId);
            auditCommand.Parameters.AddWithValue("action", TransitionAction);
            auditCommand.Parameters.Add(new NpgsqlParameter("period_start", NpgsqlDbType.Date) { Value = current.PeriodStart });
            auditCommand.Parameters.Add(new NpgsqlParameter("period_end", NpgsqlDbType.Date) { Value = current.PeriodEnd });
            auditCommand.Parameters.AddWithValue("old_status", current.Status);
            auditCommand.Parameters.AddWithValue("new_status", targetStatus);
            await auditCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return current with
        {
            Status = targetStatus,
            ClosingStartedAt = closingAt,
            ClosedAt = closedAt,
            LockedAt = lockedAt,
            UpdatedAt = now,
        };
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaEnsured) == 1) return;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists accounting_periods (
              id uuid primary key default gen_random_uuid(),
              company_id char(7) not null references companies(id) on delete cascade,
              period_start date not null,
              period_end date not null,
              status text not null default 'open',
              closing_started_at timestamptz null,
              closed_at timestamptz null,
              locked_at timestamptz null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              constraint ck_accounting_periods_status
                check (status in ('open', 'closing', 'closed', 'locked')),
              constraint ck_accounting_periods_range
                check (period_end >= period_start),
              constraint ux_accounting_periods_company_period
                unique (company_id, period_start, period_end)
            );

            create index if not exists ix_accounting_periods_company_status_start
              on accounting_periods (company_id, status, period_start);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        Volatile.Write(ref _schemaEnsured, 1);
    }

    private static async Task<List<AccountingPeriod>> ReadAllAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              id, company_id, period_start, period_end, status,
              closing_started_at, closed_at, locked_at,
              created_at, updated_at
            from accounting_periods
            where company_id = @company_id
            order by period_start asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<AccountingPeriod>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadPeriod(reader));
        }
        return rows;
    }

    private async Task<List<AccountingPeriod>> SeedCurrentFiscalYearAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        // Read the company's fiscal year end (default 12-31).
        short fiscalEndMonth;
        short fiscalEndDay;
        await using (var fyCommand = scope.CreateCommand(
                         "select fiscal_year_end_month, fiscal_year_end_day from companies where id = @company_id;"))
        {
            fyCommand.Parameters.AddWithValue("company_id", companyId.Value);
            await using var reader = await fyCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Company {companyId:D} was not found while seeding accounting periods.");
            }
            fiscalEndMonth = reader.IsDBNull(0) ? (short)12 : reader.GetInt16(0);
            fiscalEndDay = reader.IsDBNull(1) ? (short)31 : reader.GetInt16(1);
        }

        // Compute fiscal year start as the day after the previous
        // fiscal year end. Calendar year case (12-31) → fiscal year
        // starts Jan 1. The current fiscal year is whichever one
        // contains today.
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);
        var (fyStart, fyEnd) = ComputeFiscalYearBounds(today, fiscalEndMonth, fiscalEndDay);

        // Generate one period per calendar month inside the fiscal
        // year. The first / last periods may be partial months when
        // the fiscal year boundary doesn't fall on month-end.
        var periods = new List<(DateOnly Start, DateOnly End)>();
        var cursor = fyStart;
        while (cursor <= fyEnd)
        {
            var monthEnd = new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month));
            var clamped = monthEnd > fyEnd ? fyEnd : monthEnd;
            periods.Add((cursor, clamped));
            cursor = clamped.AddDays(1);
        }

        await using (var insertCommand = scope.CreateCommand(
                         """
                         insert into accounting_periods (company_id, period_start, period_end, status)
                         select @company_id, p.period_start::date, p.period_end::date, 'open'
                         from unnest(@period_starts::date[], @period_ends::date[]) as p(period_start, period_end)
                         on conflict on constraint ux_accounting_periods_company_period do nothing;
                         """))
        {
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.Add(new NpgsqlParameter("period_starts", NpgsqlDbType.Array | NpgsqlDbType.Date)
            {
                Value = periods.Select(p => p.Start).ToArray(),
            });
            insertCommand.Parameters.Add(new NpgsqlParameter("period_ends", NpgsqlDbType.Array | NpgsqlDbType.Date)
            {
                Value = periods.Select(p => p.End).ToArray(),
            });
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return await ReadAllAsync(scope, companyId, cancellationToken);
    }

    /// <summary>
    /// Returns the fiscal year (start, end) that contains
    /// <paramref name="reference"/>. End is the next occurrence of
    /// <paramref name="fiscalEndMonth"/>/<paramref name="fiscalEndDay"/>
    /// on or after <paramref name="reference"/>; start is the day
    /// after the prior fiscal year end. Calendar-year defaults
    /// (12-31) collapse to (Jan 1, Dec 31) of the reference year.
    /// </summary>
    public static (DateOnly Start, DateOnly End) ComputeFiscalYearBounds(
        DateOnly reference, short fiscalEndMonth, short fiscalEndDay)
    {
        // Construct the candidate fiscal-year-end for the same year
        // as the reference. Clamp to month length (e.g. requested
        // 02-30 lands on 02-28/29).
        var endMonthDays = DateTime.DaysInMonth(reference.Year, fiscalEndMonth);
        var endDayClamped = (int)Math.Min(fiscalEndDay, (short)endMonthDays);
        var sameYearEnd = new DateOnly(reference.Year, fiscalEndMonth, endDayClamped);

        DateOnly fyEnd;
        if (reference <= sameYearEnd)
        {
            fyEnd = sameYearEnd;
        }
        else
        {
            // Reference is past this year's end → roll into next year.
            var nextYearDays = DateTime.DaysInMonth(reference.Year + 1, fiscalEndMonth);
            var nextDayClamped = (int)Math.Min(fiscalEndDay, (short)nextYearDays);
            fyEnd = new DateOnly(reference.Year + 1, fiscalEndMonth, nextDayClamped);
        }

        var fyStart = fyEnd.AddDays(1).AddYears(-1);
        return (fyStart, fyEnd);
    }

    private static AccountingPeriod ReadPeriod(NpgsqlDataReader reader)
    {
        return new AccountingPeriod(
            Id: reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            PeriodStart: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_start")),
            PeriodEnd: reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_end")),
            Status: reader.GetString(reader.GetOrdinal("status")),
            ClosingStartedAt: reader.IsDBNull(reader.GetOrdinal("closing_started_at"))
                ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("closing_started_at")),
            ClosedAt: reader.IsDBNull(reader.GetOrdinal("closed_at"))
                ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("closed_at")),
            LockedAt: reader.IsDBNull(reader.GetOrdinal("locked_at"))
                ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("locked_at")),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
    }
}
