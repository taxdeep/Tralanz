using System.Text.Json;
using Modules.CompanyAccess.Memberships;
using Npgsql;

namespace Infrastructure.PostgreSQL.CompanyAccess;

public sealed class PostgreSqlCompanyMembershipGovernanceStore(
    PostgreSqlConnectionFactory connections) : ICompanyMembershipGovernanceStore
{
    public async Task<CompanyMembershipRoleChangeResult?> ChangeRoleFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        string role,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var target = await ReadTargetForUpdateAsync(
            connection,
            transaction,
            companyId,
            membershipId,
            cancellationToken);

        if (target is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!target.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Inactive company memberships cannot receive role changes.");
        }

        if (target.Role == "owner" && role != "owner")
        {
            var activeOwnerCount = await CountAndLockActiveOwnersAsync(
                connection,
                transaction,
                companyId,
                cancellationToken);

            if (activeOwnerCount <= 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("A company must always retain at least one active owner.");
            }
        }

        var updatedAt = DateTimeOffset.UtcNow;
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update company_memberships
                set role = @role,
                    updated_at = now()
                where company_id = @company_id
                  and id = @membership_id;
                """;
            updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateCommand.Parameters.AddWithValue("membership_id", membershipId);
            updateCommand.Parameters.AddWithValue("role", role);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditLogAsync(
            connection,
            transaction,
            companyId,
            membershipId,
            sysAdminAccountId,
            target,
            role,
            reason,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CompanyMembershipRoleChangeResult
        {
            CompanyId = companyId,
            MembershipId = membershipId,
            AccountId = target.UserId,
            Email = target.Email,
            Username = target.Username,
            PreviousRole = target.Role,
            Role = role,
            Reason = reason,
            UpdatedAtUtc = updatedAt
        };
    }

    private static async Task<MembershipTarget?> ReadTargetForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              m.id,
              m.company_id,
              m.user_id,
              m.role,
              m.is_active,
              u.email,
              coalesce(u.username, '') as username
            from company_memberships m
            inner join users u on u.id = m.user_id
            where m.company_id = @company_id
              and m.id = @membership_id
            for update of m;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("membership_id", membershipId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MembershipTarget(
            reader.GetGuid(reader.GetOrdinal("id")),
            UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
            reader.GetString(reader.GetOrdinal("email")).Trim(),
            reader.GetString(reader.GetOrdinal("username")).Trim(),
            reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant(),
            reader.GetBoolean(reader.GetOrdinal("is_active")));
    }

    private static async Task<int> CountAndLockActiveOwnersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from company_memberships
            where company_id = @company_id
              and role = 'owner'
              and is_active = true
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
        }

        return count;
    }

    private static async Task InsertAuditLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid membershipId,
        UserId? sysAdminAccountId,
        MembershipTarget target,
        string role,
        string reason,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                CompanyId = companyId,
                MembershipId = membershipId,
                TargetUserId = target.UserId,
                PreviousRole = target.Role,
                Role = role,
                Reason = reason
            });

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into audit_logs (
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload
            )
            values (
              @id,
              @company_id,
              'sysadmin',
              @actor_id,
              'company_membership',
              @entity_id,
              'membership_role_changed',
              @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_id", sysAdminAccountId.HasValue ? (object)sysAdminAccountId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", membershipId.ToString("D"));
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record MembershipTarget(
        Guid MembershipId,
        UserId UserId,
        string Email,
        string Username,
        string Role,
        bool IsActive);
}
