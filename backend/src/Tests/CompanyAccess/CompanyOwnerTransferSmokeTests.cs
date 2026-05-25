using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.Memberships;

namespace Tests.CompanyAccess;

/// <summary>
/// Integration smoke tests for the business-side owner transfer
/// pathway introduced in PR-4B. Exercises both happy path (Owner
/// transfers to active member; flags swap; audit logged) and the
/// rejection rules (caller is not Owner, target is inactive, target
/// is non-member, self-transfer).
///
/// The endpoint-layer permission gate
/// (<c>IPermissionEvaluator.CanPerformOwnerOnlyActionAsync</c>) is
/// out of scope here — it lands in PR-4C/4E. These tests verify the
/// workflow + store enforce data invariants under direct invocation.
/// </summary>
public sealed class CompanyOwnerTransferSmokeTests
{
    [Fact]
    public async Task TransferOwnershipFromOwnerAsync_HappyPath_SwapsFlagsAndLogsAudit()
    {
        var seed = await SeedScenarioAsync();
        var store = new PostgreSqlCompanyMembershipGovernanceStore(seed.Connections);
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        try
        {
            var result = await workflow.TransferOwnershipFromOwnerAsync(
                seed.CompanyId,
                seed.OwnerUserId,
                seed.UserAUserId,
                "Ownership rotated for review.",
                CancellationToken.None);

            Assert.Equal(seed.OwnerUserId, result.FromUserId);
            Assert.Equal(seed.UserAUserId, result.ToUserId);
            Assert.Equal("Ownership rotated for review.", result.Reason);

            // Re-read the two memberships and confirm both flags
            // swapped + role column kept in sync for legacy callers.
            var snapshot = await ReadMembershipFlagsAsync(seed);
            Assert.False(snapshot.OwnerFlagOnOriginalOwner);
            Assert.Equal("user", snapshot.RoleOnOriginalOwner);
            Assert.True(snapshot.OwnerFlagOnUserA);
            Assert.Equal("owner", snapshot.RoleOnUserA);

            // Audit row written with actor_type='business_user' and
            // the new owner's membership id as entity_id.
            var auditCount = await CountAuditAsync(seed, "ownership_transferred", "business_user");
            Assert.Equal(1, auditCount);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task TransferOwnershipFromOwnerAsync_RejectsWhenCallerIsNotOwner()
    {
        var seed = await SeedScenarioAsync();
        var store = new PostgreSqlCompanyMembershipGovernanceStore(seed.Connections);
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        try
        {
            // User A is a regular member, not the Owner. Even though
            // the endpoint layer should have rejected the caller via
            // IPermissionEvaluator, the store's defensive check
            // re-verifies under the row lock.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workflow.TransferOwnershipFromOwnerAsync(
                    seed.CompanyId,
                    seed.UserAUserId,
                    seed.UserBUserId,
                    "Attempted hijack.",
                    CancellationToken.None));

            Assert.Contains("no longer the active owner", ex.Message, StringComparison.OrdinalIgnoreCase);

            // No flags moved.
            var snapshot = await ReadMembershipFlagsAsync(seed);
            Assert.True(snapshot.OwnerFlagOnOriginalOwner);
            Assert.False(snapshot.OwnerFlagOnUserA);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task TransferOwnershipFromOwnerAsync_RejectsInactiveTarget()
    {
        var seed = await SeedScenarioAsync();
        var store = new PostgreSqlCompanyMembershipGovernanceStore(seed.Connections);
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workflow.TransferOwnershipFromOwnerAsync(
                    seed.CompanyId,
                    seed.OwnerUserId,
                    seed.InactiveUserId,
                    "Try to transfer to inactive member.",
                    CancellationToken.None));

            Assert.Contains("inactive", ex.Message, StringComparison.OrdinalIgnoreCase);

            var snapshot = await ReadMembershipFlagsAsync(seed);
            Assert.True(snapshot.OwnerFlagOnOriginalOwner);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task TransferOwnershipFromOwnerAsync_RejectsSelfTransfer()
    {
        var seed = await SeedScenarioAsync();
        var store = new PostgreSqlCompanyMembershipGovernanceStore(seed.Connections);
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workflow.TransferOwnershipFromOwnerAsync(
                    seed.CompanyId,
                    seed.OwnerUserId,
                    seed.OwnerUserId,
                    string.Empty,
                    CancellationToken.None));

            Assert.Contains("yourself", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [Fact]
    public async Task TransferOwnershipFromOwnerAsync_ReturnsNullWhenTargetNotMember()
    {
        var seed = await SeedScenarioAsync();
        var store = new PostgreSqlCompanyMembershipGovernanceStore(seed.Connections);
        var workflow = new CompanyMembershipGovernanceWorkflow(store);

        try
        {
            // Stranger user_id is not a member of this company. The
            // store's `ReadBothByUserIdForTransferAsync` returns only
            // the rows that exist, so toRow is null → store returns
            // null → workflow throws a clear message.
            var stranger = UserId.FromOrdinal(seed.OwnerOrdinal + 50);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workflow.TransferOwnershipFromOwnerAsync(
                    seed.CompanyId,
                    seed.OwnerUserId,
                    stranger,
                    "Try to transfer to non-member.",
                    CancellationToken.None));

            Assert.Contains("not an active member", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        UserId OwnerUserId,
        Guid OwnerMembershipId,
        UserId UserAUserId,
        Guid UserAMembershipId,
        UserId UserBUserId,
        Guid UserBMembershipId,
        UserId InactiveUserId);

    private sealed record MembershipFlagsSnapshot(
        bool OwnerFlagOnOriginalOwner,
        string RoleOnOriginalOwner,
        bool OwnerFlagOnUserA,
        string RoleOnUserA);

    private static async Task<SeedScenario> SeedScenarioAsync()
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());

        var rand = Random.Shared.Next(2000, 2999);
        var companyId = CompanyId.FromOrdinal(rand);
        var ownerUserId = UserId.FromOrdinal(rand);
        var userAUserId = UserId.FromOrdinal(rand + 1);
        var userBUserId = UserId.FromOrdinal(rand + 2);
        var inactiveUserId = UserId.FromOrdinal(rand + 3);
        var ownerMembershipId = Guid.NewGuid();
        var userAMembershipId = Guid.NewGuid();
        var userBMembershipId = Guid.NewGuid();
        var inactiveMembershipId = Guid.NewGuid();

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
              @company_id, @entity_number, 'OwnerTransferSmoke Co.',
              'USD', false, 'active'
            );

            insert into users (id, email, username, password_hash, status)
            values
              (@owner_id,    @owner_email,    'owner.transfer',  'hashed', 'active'),
              (@user_a_id,   @user_a_email,   'user.a.transfer', 'hashed', 'active'),
              (@user_b_id,   @user_b_email,   'user.b.transfer', 'hashed', 'active'),
              (@inactive_id, @inactive_email, 'user.x.transfer', 'hashed', 'active');

            insert into company_memberships (
              id, company_id, user_id, role, permissions, is_active, is_owner
            ) values
              (@owner_membership_id,    @company_id, @owner_id,    'owner', '[]'::jsonb, true,  true),
              (@user_a_membership_id,   @company_id, @user_a_id,   'user',  '[]'::jsonb, true,  false),
              (@user_b_membership_id,   @company_id, @user_b_id,   'user',  '[]'::jsonb, true,  false),
              (@inactive_membership_id, @company_id, @inactive_id, 'user',  '[]'::jsonb, false, false);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_id", ownerUserId.Value);
        command.Parameters.AddWithValue("user_a_id", userAUserId.Value);
        command.Parameters.AddWithValue("user_b_id", userBUserId.Value);
        command.Parameters.AddWithValue("inactive_id", inactiveUserId.Value);
        command.Parameters.AddWithValue("owner_membership_id", ownerMembershipId);
        command.Parameters.AddWithValue("user_a_membership_id", userAMembershipId);
        command.Parameters.AddWithValue("user_b_membership_id", userBMembershipId);
        command.Parameters.AddWithValue("inactive_membership_id", inactiveMembershipId);
        command.Parameters.AddWithValue("owner_email",    $"{ownerUserId.Value}@owner-transfer.test");
        command.Parameters.AddWithValue("user_a_email",   $"{userAUserId.Value}@owner-transfer.test");
        command.Parameters.AddWithValue("user_b_email",   $"{userBUserId.Value}@owner-transfer.test");
        command.Parameters.AddWithValue("inactive_email", $"{inactiveUserId.Value}@owner-transfer.test");
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(
            connections, companyId, rand,
            ownerUserId, ownerMembershipId,
            userAUserId, userAMembershipId,
            userBUserId, userBMembershipId,
            inactiveUserId);
    }

    private static async Task<MembershipFlagsSnapshot> ReadMembershipFlagsAsync(SeedScenario seed)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select user_id, is_owner, role
              from company_memberships
             where company_id = @company_id
               and user_id in (@owner_id, @user_a_id);
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("owner_id", seed.OwnerUserId.Value);
        command.Parameters.AddWithValue("user_a_id", seed.UserAUserId.Value);

        bool ownerFlag = false, userAFlag = false;
        string ownerRole = string.Empty, userARole = string.Empty;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            var uid = reader.GetString(0);
            if (uid == seed.OwnerUserId.Value)
            {
                ownerFlag = reader.GetBoolean(1);
                ownerRole = reader.GetString(2);
            }
            else
            {
                userAFlag = reader.GetBoolean(1);
                userARole = reader.GetString(2);
            }
        }

        return new MembershipFlagsSnapshot(ownerFlag, ownerRole, userAFlag, userARole);
    }

    private static async Task<int> CountAuditAsync(SeedScenario seed, string action, string actorType)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)::int
              from audit_logs
             where company_id = @company_id
               and action = @action
               and actor_type = @actor_type;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor_type", actorType);
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
            seed.OwnerUserId.Value, seed.UserAUserId.Value,
            seed.UserBUserId.Value, seed.InactiveUserId.Value,
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
