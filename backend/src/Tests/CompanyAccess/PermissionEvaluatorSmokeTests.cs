using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.Permissions;

namespace Tests.CompanyAccess;

/// <summary>
/// Integration smoke tests for <see cref="PostgreSqlPermissionEvaluator"/>.
/// Asserts the foundation invariants from the PR-4A spec: Owner
/// bypass, explicit-grant gate, Owner-only hard-coded actions, and
/// the eight CanGrant hard rules. Tests hit a real PG so they will
/// only run in environments where the database is reachable.
/// </summary>
public sealed class PermissionEvaluatorSmokeTests
{
    [Fact]
    public async Task IsActiveMemberAndIsOwner_ReflectMembershipState()
    {
        var seed = await SeedScenarioAsync();
        var evaluator = new PostgreSqlPermissionEvaluator(seed.Connections);

        try
        {
            // Owner
            Assert.True(await evaluator.IsActiveMemberAsync(seed.CompanyId, seed.OwnerId, default));
            Assert.True(await evaluator.IsOwnerAsync(seed.CompanyId, seed.OwnerId, default));

            // Active non-Owner
            Assert.True(await evaluator.IsActiveMemberAsync(seed.CompanyId, seed.UserAId, default));
            Assert.False(await evaluator.IsOwnerAsync(seed.CompanyId, seed.UserAId, default));

            // Inactive member: not active, not owner
            Assert.False(await evaluator.IsActiveMemberAsync(seed.CompanyId, seed.InactiveUserId, default));
            Assert.False(await evaluator.IsOwnerAsync(seed.CompanyId, seed.InactiveUserId, default));

            // Stranger (not a member at all)
            var stranger = UserId.FromOrdinal(seed.OwnerOrdinal + 99);
            Assert.False(await evaluator.IsActiveMemberAsync(seed.CompanyId, stranger, default));
            Assert.False(await evaluator.IsOwnerAsync(seed.CompanyId, stranger, default));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task CanPerformOwnerOnlyAction_AllowsOnlyOwner_AndOnlyKnownActions()
    {
        var seed = await SeedScenarioAsync();
        var evaluator = new PostgreSqlPermissionEvaluator(seed.Connections);

        try
        {
            foreach (var action in OwnerOnlyActions.All)
            {
                Assert.True(
                    await evaluator.CanPerformOwnerOnlyActionAsync(seed.CompanyId, seed.OwnerId, action, default),
                    $"Owner must be allowed for {action}");
                Assert.False(
                    await evaluator.CanPerformOwnerOnlyActionAsync(seed.CompanyId, seed.UserAId, action, default),
                    $"Non-Owner must be denied for {action}");
            }

            // Unrecognised action: even Owner is denied (closed list).
            Assert.False(await evaluator.CanPerformOwnerOnlyActionAsync(
                seed.CompanyId, seed.OwnerId, "company.something_made_up", default));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task CanAsync_BusinessTokenPath_OwnerBypassAndExplicitGrant()
    {
        var seed = await SeedScenarioAsync();
        var evaluator = new PostgreSqlPermissionEvaluator(seed.Connections);

        try
        {
            const string highRiskToken = "ar.invoice.post";
            const string lowRiskToken = "ap.bill.view";

            // Owner can do anything (implied-all).
            Assert.True(await evaluator.CanAsync(seed.CompanyId, seed.OwnerId, highRiskToken, default));
            Assert.True(await evaluator.CanAsync(seed.CompanyId, seed.OwnerId, lowRiskToken, default));

            // User A was granted ap.bill.view, nothing else.
            Assert.True(await evaluator.CanAsync(seed.CompanyId, seed.UserAId, lowRiskToken, default));
            Assert.False(await evaluator.CanAsync(seed.CompanyId, seed.UserAId, highRiskToken, default));

            // User B has no grants at all.
            Assert.False(await evaluator.CanAsync(seed.CompanyId, seed.UserBId, lowRiskToken, default));
            Assert.False(await evaluator.CanAsync(seed.CompanyId, seed.UserBId, highRiskToken, default));

            // Owner-only token via business path must always be false —
            // callers must use CanPerformOwnerOnlyActionAsync.
            Assert.False(await evaluator.CanAsync(
                seed.CompanyId, seed.OwnerId, OwnerOnlyActions.CompanyMakeInactive, default));
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task CanGrant_EnforcesAllEightHardRules()
    {
        var seed = await SeedScenarioAsync();
        var evaluator = new PostgreSqlPermissionEvaluator(seed.Connections);

        try
        {
            const string assignable = "ap.bill.view";
            const string anotherAssignable = "ar.invoice.view";

            // Owner can grant any assignable token to any active non-Owner.
            Assert.Equal(GrantAuthorityResult.Allowed,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.OwnerId, seed.UserAId, assignable, default));

            // 1. Self-grant blocked (even for Owner; even for User with authority).
            Assert.Equal(GrantAuthorityResult.DeniedSelfGrant,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.OwnerId, seed.OwnerId, assignable, default));
            Assert.Equal(GrantAuthorityResult.DeniedSelfGrant,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.UserAId, seed.UserAId, assignable, default));

            // 2. Actor not active member.
            var stranger = UserId.FromOrdinal(seed.OwnerOrdinal + 88);
            Assert.Equal(GrantAuthorityResult.DeniedActorNotActiveMember,
                await evaluator.CanGrantAsync(seed.CompanyId, stranger, seed.UserAId, assignable, default));

            // 3. Target not active member.
            Assert.Equal(GrantAuthorityResult.DeniedTargetNotActiveMember,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.OwnerId, seed.InactiveUserId, assignable, default));

            // 4. Target is Owner.
            Assert.Equal(GrantAuthorityResult.DeniedTargetIsOwner,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.UserAId, seed.OwnerId, assignable, default));

            // 5. Unknown token (not in permission_registry).
            Assert.Equal(GrantAuthorityResult.DeniedTokenNotInRegistry,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.OwnerId, seed.UserAId, "nonexistent.token", default));

            // 6. Token not assignable (one of the four Owner-only actions).
            foreach (var ownerOnlyAction in OwnerOnlyActions.All)
            {
                Assert.Equal(GrantAuthorityResult.DeniedTokenNotAssignable,
                    await evaluator.CanGrantAsync(seed.CompanyId, seed.OwnerId, seed.UserAId, ownerOnlyAction, default));
            }

            // 7. Non-Owner without grant authority.
            //    User A has grant authority for `ap.bill.view` only (seeded below).
            //    A tries to grant `ar.invoice.view` to B → denied.
            Assert.Equal(GrantAuthorityResult.DeniedActorMissingGrantAuthority,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.UserAId, seed.UserBId, anotherAssignable, default));

            // 8. Non-Owner WITH grant authority for the specific token can grant.
            //    User A has grant authority for `ap.bill.view` → grant to B succeeds.
            Assert.Equal(GrantAuthorityResult.Allowed,
                await evaluator.CanGrantAsync(seed.CompanyId, seed.UserAId, seed.UserBId, assignable, default));
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
        int OwnerOrdinal,
        UserId OwnerId,
        UserId UserAId,      // active, has grant authority for ap.bill.view + business permission ap.bill.view
        UserId UserBId,      // active, no grants
        UserId InactiveUserId);

    private static async Task<SeedScenario> SeedScenarioAsync()
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());

        // Use random ordinals so multiple test runs don't collide on
        // (company_id, user_id) keys. The 800-1799 range stays clear
        // of the small-ordinal seeds other smoke tests use.
        var rand = Random.Shared.Next(800, 1799);
        var companyId = CompanyId.FromOrdinal(rand);
        var ownerId = UserId.FromOrdinal(rand);
        var userAId = UserId.FromOrdinal(rand + 1);
        var userBId = UserId.FromOrdinal(rand + 2);
        var inactiveUserId = UserId.FromOrdinal(rand + 3);

        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            -- Ensure permissions jsonb column exists (legacy compat).
            alter table company_memberships
              add column if not exists permissions jsonb not null default '[]'::jsonb;

            insert into companies (
              id, entity_number, legal_name, base_currency_code,
              multi_currency_enabled, status
            ) values (
              @company_id, @entity_number, 'PermissionEvaluatorSmokeTest Co.',
              'USD', false, 'active'
            );

            insert into users (id, email, username, password_hash, status)
            values
              (@owner_id,    @owner_email,    'owner.eval',    'hashed-password', 'active'),
              (@user_a_id,   @user_a_email,   'user.a.eval',   'hashed-password', 'active'),
              (@user_b_id,   @user_b_email,   'user.b.eval',   'hashed-password', 'active'),
              (@inactive_id, @inactive_email, 'user.inact.eval','hashed-password', 'active');

            insert into company_memberships (
              id, company_id, user_id, role, permissions, is_active, is_owner, status
            ) values
              (gen_random_uuid(), @company_id, @owner_id,    'owner', '[]'::jsonb, true,  true,  'active'),
              (gen_random_uuid(), @company_id, @user_a_id,   'user',  '[]'::jsonb, true,  false, 'active'),
              (gen_random_uuid(), @company_id, @user_b_id,   'user',  '[]'::jsonb, true,  false, 'active'),
              (gen_random_uuid(), @company_id, @inactive_id, 'user',  '[]'::jsonb, false, false, 'inactive');

            -- User A is granted business permission ap.bill.view, AND
            -- grant authority for the same token. Two orthogonal rows
            -- so the test can distinguish "I can use this" vs "I can
            -- delegate this".
            insert into company_user_permissions
              (company_id, user_id, permission_token, granted_by_user_id, is_active)
            values
              (@company_id, @user_a_id, 'ap.bill.view', @owner_id, true);

            insert into company_user_permission_grant_authorities
              (company_id, user_id, grantable_permission_token,
               can_grant, can_revoke, granted_by_owner_user_id, is_active)
            values
              (@company_id, @user_a_id, 'ap.bill.view',
               true, true, @owner_id, true);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_id", ownerId.Value);
        command.Parameters.AddWithValue("user_a_id", userAId.Value);
        command.Parameters.AddWithValue("user_b_id", userBId.Value);
        command.Parameters.AddWithValue("inactive_id", inactiveUserId.Value);
        command.Parameters.AddWithValue("owner_email",    $"{ownerId.Value}@evaluator.test");
        command.Parameters.AddWithValue("user_a_email",   $"{userAId.Value}@evaluator.test");
        command.Parameters.AddWithValue("user_b_email",   $"{userBId.Value}@evaluator.test");
        command.Parameters.AddWithValue("inactive_email", $"{inactiveUserId.Value}@evaluator.test");
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(
            connections, companyId, rand,
            ownerId, userAId, userBId, inactiveUserId);
    }

    private static async Task CleanupAsync(SeedScenario seed)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from company_user_permission_grant_authorities where company_id = @company_id;
            delete from company_user_permissions where company_id = @company_id;
            delete from company_memberships where company_id = @company_id;
            delete from companies where id = @company_id;
            delete from users where id = any(@user_ids);
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("user_ids", new[]
        {
            seed.OwnerId.Value, seed.UserAId.Value, seed.UserBId.Value, seed.InactiveUserId.Value,
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
