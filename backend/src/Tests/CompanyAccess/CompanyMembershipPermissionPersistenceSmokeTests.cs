using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.Memberships;

namespace Tests.CompanyAccess;

public sealed class CompanyMembershipPermissionPersistenceSmokeTests
{
    [Fact]
    public async Task SavePermissionsAsync_AppendsMembershipPermissionAuditLog()
    {
        var companyId = CompanyId.FromOrdinal(101);
        var ownerUserId = UserId.FromOrdinal(101);
        var targetUserId = UserId.FromOrdinal(102);
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
                ["ar.invoice.view", "ap.bill.view"],
                CancellationToken.None);

            // X-4: the store sorts permission tokens with StringComparer.Ordinal
            // (PostgreSqlCompanyMembershipPermissionStore.ParsePermissionTokens /
            //  ReadTokens / InsertAuditLogAsync), so the read-back order is
            // alphabetical ("ap.bill.view" before "ar.invoice.view") even when
            // the caller supplied them in a different order.
            Assert.Equal(["ap.bill.view", "ar.invoice.view"], result.Membership.PermissionTokens);

            var audits = await workflow.ListRecentAuditAsync(companyId, 10, CancellationToken.None);
            var audit = audits.SingleOrDefault(record => record.MembershipId == targetMembershipId);

            Assert.NotNull(audit);
            Assert.Equal(ownerUserId, audit!.ActorUserId);
            Assert.Equal(targetUserId, audit.TargetUserId);
            // X-4: SeedAsync now suffixes the username with the UserId for
            // per-run uniqueness ('member.audit.' || @target_user_id), so
            // the audit's TargetDisplayName carries the suffix too.
            Assert.Equal($"member.audit.{targetUserId.Value}", audit.TargetDisplayName);
            Assert.Equal("user", audit.TargetRole);
            Assert.Equal(["reports"], audit.PreviousPermissionTokens);
            Assert.Equal(["ap.bill.view", "ar.invoice.view"], audit.SavedPermissionTokens);
            Assert.Equal(["ap.bill.view", "ar.invoice.view"], audit.AddedPermissionTokens);
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
              id uuid primary key default gen_random_uuid(),
              company_id char(7) not null,
              actor_type text not null,
              actor_id char(7) null,
              entity_type text not null,
              entity_id text not null,
              action text not null,
              payload jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now()
            );

            do $$
            begin
              if exists (
                select 1
                from information_schema.columns
                where table_schema = 'public'
                  and table_name = 'audit_logs'
                  and column_name = 'actor_id'
                  and udt_name = 'uuid'
              ) then
                alter table audit_logs
                  alter column actor_id type char(7) using null;
              end if;

              if exists (
                select 1
                from information_schema.columns
                where table_schema = 'public'
                  and table_name = 'audit_logs'
                  and column_name = 'entity_id'
                  and udt_name = 'uuid'
              ) then
                alter table audit_logs
                  alter column entity_id type text using entity_id::text;
              end if;
            end $$;

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

            -- X-4 test-isolation: append per-run UserId for unique username.
            insert into users (id, email, username, password_hash, status)
            values
              (@owner_user_id,  @owner_email,  'owner.audit.'  || @owner_user_id,  'hashed-password', 'active'),
              (@target_user_id, @target_email, 'member.audit.' || @target_user_id, 'hashed-password', 'active');

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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_user_id", ownerUserId.Value);
        command.Parameters.AddWithValue("target_user_id", targetUserId.Value);
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("user_ids", new[] { ownerUserId.Value, targetUserId.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildEntityNumber()
    {
        var ordinal = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 60_466_176;
        return EntityNumber.Create(2099, ordinal).Value;
    }
}
