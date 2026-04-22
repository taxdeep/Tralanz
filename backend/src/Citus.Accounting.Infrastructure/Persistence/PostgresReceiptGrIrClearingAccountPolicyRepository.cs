using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresReceiptGrIrClearingAccountPolicyRepository : IReceiptGrIrClearingAccountPolicyRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresReceiptGrIrClearingAccountPolicyRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<Guid?> GetDefaultGrIrClearingAccountIdAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);
        await EnsureSchemaAsync(scope, cancellationToken);

        await using var command = scope.CreateCommand(
            """
            select policy.grir_clearing_account_id
            from receipt_grir_clearing_account_policies policy
            join accounts account
              on account.company_id = policy.company_id
             and account.id = policy.grir_clearing_account_id
             and account.is_active = true
             and account.root_type = 'liability'
            where policy.company_id = @company_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid accountId ? accountId : null;
    }

    public async Task SaveDefaultGrIrClearingAccountAsync(
        CompanyId companyId,
        UserId userId,
        Guid grIrClearingAccountId,
        CancellationToken cancellationToken)
    {
        if (grIrClearingAccountId == Guid.Empty)
        {
            throw new ArgumentException("GR/IR clearing account id is required.", nameof(grIrClearingAccountId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);
        await EnsureSchemaAsync(scope, cancellationToken);
        await EnsureActiveAccountAsync(scope, companyId.Value, grIrClearingAccountId, cancellationToken);

        await using var command = scope.CreateCommand(
            """
            insert into receipt_grir_clearing_account_policies (
              company_id,
              grir_clearing_account_id,
              updated_by_user_id,
              updated_at
            )
            values (
              @company_id,
              @grir_clearing_account_id,
              @updated_by_user_id,
              now()
            )
            on conflict (company_id)
            do update set
              grir_clearing_account_id = excluded.grir_clearing_account_id,
              updated_by_user_id = excluded.updated_by_user_id,
              updated_at = now();
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("grir_clearing_account_id", grIrClearingAccountId);
        command.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureActiveAccountAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select exists (
              select 1
              from companies company
              join accounts account
                on account.company_id = company.id
               and account.id = @account_id
               and account.is_active = true
               and account.root_type = 'liability'
              where company.id = @company_id
                and company.status = 'active'
            );
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("account_id", accountId);

        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "Default GR/IR clearing account must be an active liability account in the active company.");
        }
    }

    private static async Task EnsureSchemaAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            create table if not exists receipt_grir_clearing_account_policies (
              company_id uuid primary key references companies(id) on delete cascade,
              grir_clearing_account_id uuid not null references accounts(id),
              updated_by_user_id uuid not null,
              updated_at timestamptz not null default now()
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
