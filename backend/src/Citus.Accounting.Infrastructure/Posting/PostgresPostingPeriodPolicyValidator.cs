using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Periods;
using Citus.Accounting.Infrastructure.Persistence;

namespace Citus.Accounting.Infrastructure.Posting;

/// <summary>
/// M7 iter 2 implementation. One SELECT per post against
/// <c>accounting_periods</c>; the existing <c>(company_id, status,
/// period_start)</c> index keeps it cheap. Period membership uses
/// <c>BETWEEN period_start AND period_end</c> (inclusive on both
/// sides — period_end is the last day-of-month that belongs to the
/// period).
/// </summary>
public sealed class PostgresPostingPeriodPolicyValidator : IPostingPeriodPolicyValidator
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

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

        // Look up the period containing the effective date. If none
        // exists for the company yet (table not bootstrapped, or
        // effective date outside the seeded fiscal year), let the
        // post through — V1 doesn't enforce historical / future
        // boundaries unless the operator explicitly seeded a closed
        // period there.
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
