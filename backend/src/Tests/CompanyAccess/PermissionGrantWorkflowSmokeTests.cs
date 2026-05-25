using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.Permissions;

namespace Tests.CompanyAccess;

/// <summary>
/// Integration smoke tests for the PR-4E grant / revoke workflow.
/// Covers the happy path (Owner grants and revokes), the idempotency
/// branch (granting an active token is a no-op), and the rejection
/// path (non-Owner without grant authority is refused with the right
/// reason code). Also verifies audit_logs rows are written with
/// actor_type='business_user'.
/// </summary>
public sealed class PermissionGrantWorkflowSmokeTests
{
    [Fact]
    public async Task Owner_CanGrantAndRevoke_AndAuditLogsBothEvents()
    {
        var seed = await SeedScenarioAsync();
        var (workflow, _) = BuildWorkflow(seed);

        try
        {
            // Grant
            var grantResult = await workflow.GrantAsync(
                seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                "ar.invoice.view", CancellationToken.None);

            Assert.True(grantResult.Applied);
            Assert.Equal(GrantAuthorityResult.Allowed, grantResult.ResultCode);
            Assert.Equal("grant", grantResult.Action);
            Assert.True(await ActiveGrantExistsAsync(seed, seed.UserAUserId, "ar.invoice.view"));
            Assert.Equal(1, await CountAuditAsync(seed, "permission_granted"));

            // Revoke
            var revokeResult = await workflow.RevokeAsync(
                seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                "ar.invoice.view", CancellationToken.None);

            Assert.True(revokeResult.Applied);
            Assert.Equal(GrantAuthorityResult.Allowed, revokeResult.ResultCode);
            Assert.Equal("revoke", revokeResult.Action);
            Assert.False(await ActiveGrantExistsAsync(seed, seed.UserAUserId, "ar.invoice.view"));
            Assert.Equal(1, await CountAuditAsync(seed, "permission_revoked"));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task Owner_GrantingTwice_IsIdempotentNoOp()
    {
        var seed = await SeedScenarioAsync();
        var (workflow, _) = BuildWorkflow(seed);

        try
        {
            var first = await workflow.GrantAsync(
                seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                "ap.bill.view", CancellationToken.None);
            Assert.True(first.Applied);

            var second = await workflow.GrantAsync(
                seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                "ap.bill.view", CancellationToken.None);
            Assert.False(second.Applied);
            // Still Allowed — the workflow let it through; the store
            // detected the existing active row and reported back.
            Assert.Equal(GrantAuthorityResult.Allowed, second.ResultCode);

            // Only one audit row, not two.
            Assert.Equal(1, await CountAuditAsync(seed, "permission_granted"));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task NonOwnerWithoutGrantAuthority_IsRejectedWithReason()
    {
        var seed = await SeedScenarioAsync();
        var (workflow, _) = BuildWorkflow(seed);

        try
        {
            // User A (non-Owner, no grant-authority row) tries to grant
            // ar.invoice.view to User B. Must be rejected with the
            // precise reason code so the UI can render it.
            var result = await workflow.GrantAsync(
                seed.CompanyId, seed.UserAUserId, seed.UserBUserId,
                "ar.invoice.view", CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(GrantAuthorityResult.DeniedActorMissingGrantAuthority, result.ResultCode);

            // No grant should have landed and no audit row written.
            Assert.False(await ActiveGrantExistsAsync(seed, seed.UserBUserId, "ar.invoice.view"));
            Assert.Equal(0, await CountAuditAsync(seed, "permission_granted"));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task Owner_CannotGrantOwnerOnlyAction()
    {
        var seed = await SeedScenarioAsync();
        var (workflow, _) = BuildWorkflow(seed);

        try
        {
            // Owner-only actions are never assignable via the grant
            // path — even Owner gets the DeniedTokenNotAssignable
            // result because the registry row has is_assignable=false.
            var result = await workflow.GrantAsync(
                seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                OwnerOnlyActions.CompanyMakeInactive, CancellationToken.None);

            Assert.False(result.Applied);
            Assert.Equal(GrantAuthorityResult.DeniedTokenNotAssignable, result.ResultCode);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task GetUserPermissions_ReturnsOwnerFlagAndActiveGrants()
    {
        var seed = await SeedScenarioAsync();
        var (workflow, _) = BuildWorkflow(seed);

        try
        {
            // Grant two tokens to UserA, then snapshot.
            await workflow.GrantAsync(seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                "ar.invoice.view", CancellationToken.None);
            await workflow.GrantAsync(seed.CompanyId, seed.OwnerUserId, seed.UserAUserId,
                "ap.bill.view", CancellationToken.None);

            var snapshot = await workflow.GetUserPermissionsAsync(
                seed.CompanyId, seed.UserAUserId, CancellationToken.None);

            Assert.False(snapshot.IsOwner);
            Assert.Equal(2, snapshot.ActiveGrants.Count);
            Assert.Contains(snapshot.ActiveGrants, g => g.PermissionToken == "ar.invoice.view");
            Assert.Contains(snapshot.ActiveGrants, g => g.PermissionToken == "ap.bill.view");

            // Owner snapshot has IsOwner=true.
            var ownerSnapshot = await workflow.GetUserPermissionsAsync(
                seed.CompanyId, seed.OwnerUserId, CancellationToken.None);
            Assert.True(ownerSnapshot.IsOwner);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    // ---------------------------------------------------------------
    // Seed helpers
    // ---------------------------------------------------------------

    private sealed record SeedScenario(
        PostgreSqlConnectionFactory Connections,
        CompanyId CompanyId,
        UserId OwnerUserId,
        UserId UserAUserId,
        UserId UserBUserId);

    private static (IPermissionGrantWorkflow Workflow, IPermissionGrantStore Store)
        BuildWorkflow(SeedScenario seed)
    {
        var evaluator = new PostgreSqlPermissionEvaluator(seed.Connections);
        var store = new PostgreSqlPermissionGrantStore(seed.Connections);
        var workflow = new PermissionGrantWorkflow(evaluator, store);
        return (workflow, store);
    }

    private static async Task<SeedScenario> SeedScenarioAsync()
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());

        var rand = Random.Shared.Next(4000, 4999);
        var companyId = CompanyId.FromOrdinal(rand);
        var ownerUserId = UserId.FromOrdinal(rand);
        var userAUserId = UserId.FromOrdinal(rand + 1);
        var userBUserId = UserId.FromOrdinal(rand + 2);

        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            alter table company_memberships
              add column if not exists permissions jsonb not null default '[]'::jsonb;
            alter table company_memberships
              add column if not exists is_owner boolean not null default false;

            insert into companies (
              id, entity_number, legal_name, base_currency_code,
              multi_currency_enabled, status
            ) values (
              @company_id, @entity_number, 'GrantWorkflowSmoke Co.',
              'USD', false, 'active'
            );

            insert into users (id, email, username, password_hash, status)
            values
              (@owner_id,  @owner_email,  'owner.grant',  'hashed', 'active'),
              (@user_a_id, @user_a_email, 'user.a.grant', 'hashed', 'active'),
              (@user_b_id, @user_b_email, 'user.b.grant', 'hashed', 'active');

            insert into company_memberships (
              id, company_id, user_id, role, permissions, is_active, is_owner
            ) values
              (gen_random_uuid(), @company_id, @owner_id,  'owner', '[]'::jsonb, true, true),
              (gen_random_uuid(), @company_id, @user_a_id, 'user',  '[]'::jsonb, true, false),
              (gen_random_uuid(), @company_id, @user_b_id, 'user',  '[]'::jsonb, true, false);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_id", ownerUserId.Value);
        command.Parameters.AddWithValue("user_a_id", userAUserId.Value);
        command.Parameters.AddWithValue("user_b_id", userBUserId.Value);
        command.Parameters.AddWithValue("owner_email",  $"{ownerUserId.Value}@grantworkflow.test");
        command.Parameters.AddWithValue("user_a_email", $"{userAUserId.Value}@grantworkflow.test");
        command.Parameters.AddWithValue("user_b_email", $"{userBUserId.Value}@grantworkflow.test");
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(connections, companyId,
            ownerUserId, userAUserId, userBUserId);
    }

    private static async Task<bool> ActiveGrantExistsAsync(SeedScenario seed, UserId userId, string token)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists(
              select 1 from company_user_permissions
               where company_id = @company_id and user_id = @user_id
                 and permission_token = @token and is_active = true);
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("token", token);
        return (bool)(await command.ExecuteScalarAsync(CancellationToken.None) ?? false);
    }

    private static async Task<int> CountAuditAsync(SeedScenario seed, string action)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int
              from audit_logs
             where company_id = @company_id
               and action = @action
               and actor_type = 'business_user';
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("action", action);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task CleanupAsync(SeedScenario seed)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from audit_logs where company_id = @company_id;
            delete from company_user_permission_grant_authorities where company_id = @company_id;
            delete from company_user_permissions where company_id = @company_id;
            delete from company_memberships where company_id = @company_id;
            delete from companies where id = @company_id;
            delete from users where id = any(@user_ids);
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("user_ids", new[]
        {
            seed.OwnerUserId.Value, seed.UserAUserId.Value, seed.UserBUserId.Value,
        });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static string BuildEntityNumber()
    {
        var ordinal = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 60_466_176;
        return EntityNumber.Create(2099, ordinal).Value;
    }
}
