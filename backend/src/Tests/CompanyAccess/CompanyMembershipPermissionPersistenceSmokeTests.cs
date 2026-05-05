using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.Memberships;

namespace Tests.CompanyAccess;

public sealed class CompanyMembershipPermissionPersistenceSmokeTests
{
    [Fact]
    public async Task SavePermissionsAsync_AppendsMembershipPermissionAuditLog()
    {
        var companyId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var ownerMembershipId = Guid.NewGuid();
        var targetMembershipId = Guid.NewGuid();
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCompanyMembershipPermissionStore(connectionFactory);
        var workflow = new CompanyMembershipPermissionWorkflow(store);

        try
        {
            await SeedAsync(
                connectionFactory,
                companyId,
                ownerUserId,
                targetUserId,
                ownerMembershipId,
                targetMembershipId,
                CancellationToken.None);

            var result = await workflow.SavePermissionsAsync(
                companyId,
                targetMembershipId,
                ownerUserId,
                ["ap", "company_book_governance"],
                CancellationToken.None);

            Assert.Equal(["ap", "company_book_governance"], result.Membership.PermissionTokens);

            var audits = await workflow.ListRecentAuditAsync(companyId, 10, CancellationToken.None);
            var audit = audits.SingleOrDefault(record => record.MembershipId == targetMembershipId);

            Assert.NotNull(audit);
            Assert.Equal(ownerUserId, audit!.ActorUserId);
            Assert.Equal(targetUserId, audit.TargetUserId);
            Assert.Equal("member.audit", audit.TargetDisplayName);
            Assert.Equal("user", audit.TargetRole);
            Assert.Equal(["reports"], audit.PreviousPermissionTokens);
            Assert.Equal(["ap", "company_book_governance"], audit.SavedPermissionTokens);
            Assert.Equal(["ap", "company_book_governance"], audit.AddedPermissionTokens);
            Assert.Equal(["reports"], audit.RemovedPermissionTokens);
        }
        finally
        {
            await CleanupAsync(connectionFactory, companyId, ownerUserId, targetUserId, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task SeedAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId ownerUserId,
        UserId targetUserId,
        Guid ownerMembershipId,
        Guid targetMembershipId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            alter table company_memberships
              add column if not exists permissions jsonb not null default '[]'::jsonb;

            create table if not exists audit_logs (
              id uuid primary key,
              company_id uuid not null,
              actor_type text not null,
              actor_id uuid null,
              entity_type text not null,
              entity_id uuid not null,
              action text not null,
              payload jsonb not null,
              created_at timestamptz not null default now()
            );

            insert into companies (
              id,
              entity_number,
              legal_name,
              base_currency_code,
              multi_currency_enabled,
              status
            )
            values (
              @company_id,
              @entity_number,
              'CompanyAccess Permission Audit Co.',
              'USD',
              false,
              'active'
            );

            insert into users (id, email, username, password_hash, is_active)
            values
              (@owner_user_id, @owner_email, 'owner.audit', 'hashed-password', true),
              (@target_user_id, @target_email, 'member.audit', 'hashed-password', true);

            insert into company_memberships (
              id,
              company_id,
              user_id,
              role,
              permissions,
              is_active
            )
            values
              (@owner_membership_id, @company_id, @owner_user_id, 'owner', '[]'::jsonb, true),
              (@target_membership_id, @company_id, @target_user_id, 'user', '["reports"]'::jsonb, true);
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_user_id", ownerUserId);
        command.Parameters.AddWithValue("target_user_id", targetUserId);
        command.Parameters.AddWithValue("owner_membership_id", ownerMembershipId);
        command.Parameters.AddWithValue("target_membership_id", targetMembershipId);
        command.Parameters.AddWithValue("owner_email", $"{ownerUserId:N}@example.test");
        command.Parameters.AddWithValue("target_email", $"{targetUserId:N}@example.test");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CompanyId companyId,
        UserId ownerUserId,
        UserId targetUserId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from audit_logs
            where company_id = @company_id;

            delete from company_memberships
            where company_id = @company_id;

            delete from companies
            where id = @company_id;

            delete from users
            where id = any(@user_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_ids", new[] { ownerUserId, targetUserId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildEntityNumber()
    {
        var numeric = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 100_000_000;
        return $"EN2099{numeric:D8}";
    }
}
