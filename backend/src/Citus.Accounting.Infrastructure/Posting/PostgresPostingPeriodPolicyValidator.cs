using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Periods;
using Citus.Accounting.Infrastructure.Persistence;

namespace Citus.Accounting.Infrastructure.Posting;

/// <summary>
/// M7 iter 2 implementation. Two cheap queries per post:
///   1. <c>to_regclass</c> existence check on accounting_periods —
///      a deploy that never visited Settings → Accounting Periods
///      hasn't bootstrapped the table, and the validator should be
///      a no-op in that case (V1 permissive policy when no period
///      data exists).
///   2. The actual SELECT against accounting_periods, only run if
///      step 1 confirmed the table.
/// Period membership uses BETWEEN period_start AND period_end
/// (inclusive on both sides — period_end is the last day-of-month).
///
/// Two queries instead of one matters because Postgres parses the
/// real query against the catalog before WHERE evaluation; embedding
/// the to_regclass guard inside the WHERE clause does NOT prevent the
/// 'relation does not exist' parse error. The split is the only safe
/// pattern outside of dynamic SQL (DO + EXECUTE), and it's cheaper.
/// </summary>
public sealed class PostgresPostingPeriodPolicyValidator : IPostingPeriodPolicyValidator
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;
    // Cache the per-process answer; once a database has the table it
    // never goes away. Saves the to_regclass round-trip on every
    // subsequent post in the same process lifetime.
    private int _tableConfirmed;

    public PostgresPostingPeriodPolicyValidator(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task ValidateAsync(
        CompanyId companyId,
        DateOnly effectiveDate,
        string sourceType,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        if (Volatile.Read(ref _tableConfirmed) == 0)
        {
            await using var existsCommand = scope.CreateCommand(
                "select to_regclass('public.accounting_periods') is not null;");
            var existsResult = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (existsResult is not bool exists || !exists)
            {
                // Table not bootstrapped on this database yet — V1
                // permissive policy. Posts proceed without period
                // enforcement until Settings → Accounting Periods is
                // visited (which auto-seeds the table on first read).
                return;
            }
            Volatile.Write(ref _tableConfirmed, 1);
        }

        await using var command = scope.CreateCommand(
            """
            select status, period_start, period_end
            from accounting_periods
            where company_id = @company_id
              and @effective_date between period_start and period_end
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("effective_date", effectiveDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // No matching period — V1 permissive policy.
            return;
        }

        var status = reader.GetString(0);
        if (status is AccountingPeriodStatus.Closed or AccountingPeriodStatus.Locked)
        {
            var periodStart = reader.GetFieldValue<DateOnly>(1);
            var periodEnd = reader.GetFieldValue<DateOnly>(2);
            var humanStatus = status == AccountingPeriodStatus.Closed ? "closed" : "locked";
            throw new InvalidOperationException(
                $"Cannot post {sourceType} with effective date {effectiveDate:yyyy-MM-dd}: " +
                $"the period {periodStart:yyyy-MM-dd} → {periodEnd:yyyy-MM-dd} is {humanStatus}. " +
                $"Re-key into the current open period and use the memo to record the original {effectiveDate:yyyy-MM-dd} date.");
        }
    }
}
