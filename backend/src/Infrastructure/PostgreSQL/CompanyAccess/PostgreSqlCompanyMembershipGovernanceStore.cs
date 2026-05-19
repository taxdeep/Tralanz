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

        if (target.IsOwner)
        {
            // After Batch 3.5 the owner display label is governance-
            // locked: the only way to change it is the ownership
            // transfer pathway, which atomically reassigns
            // is_owner=true to a different membership and rewrites
            // both rows' role for display consistency. Direct role
            // edits would otherwise leave the partial unique index
            // happy but the UI showing "user" while the owner power
            // is still here.
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "Cannot change the role of the company owner. Transfer ownership to another member first.");
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
              m.is_owner,
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
            reader.GetBoolean(reader.GetOrdinal("is_active")),
            reader.GetBoolean(reader.GetOrdinal("is_owner")));
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
        bool IsActive,
        bool IsOwner);

    public async Task<CompanyMembershipOwnershipTransferResult?> TransferOwnershipFromSysAdminAsync(
        CompanyId companyId,
        Guid fromMembershipId,
        Guid toMembershipId,
        string reason,
        UserId? sysAdminAccountId,
        IReadOnlyList<string> newOwnerPermissions,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Lock both rows up-front to serialize concurrent transfers
        // against this company. Ordered lookup keeps the index scan
        // deterministic, but the substantive uniqueness guarantee
        // comes from uq_company_memberships_one_owner.
        var rows = await ReadBothForTransferAsync(
            connection,
            transaction,
            companyId,
            fromMembershipId,
            toMembershipId,
            cancellationToken);

        var fromRow = rows.FirstOrDefault(r => r.MembershipId == fromMembershipId);
        var toRow = rows.FirstOrDefault(r => r.MembershipId == toMembershipId);

        if (fromRow is null || toRow is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!fromRow.IsOwner)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("The source membership is not the current owner of this company.");
        }

        if (!toRow.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Cannot transfer ownership to an inactive membership.");
        }

        var transferredAt = DateTimeOffset.UtcNow;

        // Single statement that flips both flags. Within an UPDATE,
        // PG sees the post-set state at commit time, so the partial
        // unique index never observes "two owners". A concurrent
        // transfer hitting the same company races on the row lock
        // above and serializes naturally.
        await using (var swapCommand = connection.CreateCommand())
        {
            swapCommand.Transaction = transaction;
            swapCommand.CommandText =
                """
                update company_memberships
                set is_owner = case
                      when id = @to_id then true
                      when id = @from_id then false
                      else is_owner
                    end,
                    -- Keep the display label (`role`) in sync with the
                    -- authoritative is_owner flag so UIs that still
                    -- show "owner / user" stay coherent. role does
                    -- not participate in authorization decisions any
                    -- more, but it remains the human-readable badge.
                    role = case
                      when id = @to_id then 'owner'
                      when id = @from_id then 'user'
                      else role
                    end,
                    permissions = case
                      when id = @to_id then @new_owner_permissions::jsonb
                      else permissions
                    end,
                    updated_at = now()
                where company_id = @company_id
                  and id in (@from_id, @to_id);
                """;
            swapCommand.Parameters.AddWithValue("company_id", companyId.Value);
            swapCommand.Parameters.AddWithValue("from_id", fromMembershipId);
            swapCommand.Parameters.AddWithValue("to_id", toMembershipId);
            swapCommand.Parameters.AddWithValue("new_owner_permissions", JsonSerializer.Serialize(newOwnerPermissions));
            await swapCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertOwnershipTransferAuditAsync(
            connection,
            transaction,
            companyId,
            fromRow,
            toRow,
            sysAdminAccountId,
            reason,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CompanyMembershipOwnershipTransferResult
        {
            CompanyId = companyId,
            FromMembershipId = fromRow.MembershipId,
            FromUserId = fromRow.UserId,
            ToMembershipId = toRow.MembershipId,
            ToUserId = toRow.UserId,
            Reason = reason,
            TransferredAtUtc = transferredAt,
        };
    }

    private static async Task<IReadOnlyList<OwnershipTransferRow>> ReadBothForTransferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid fromMembershipId,
        Guid toMembershipId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, user_id, is_active, is_owner
            from company_memberships
            where company_id = @company_id
              and id in (@from_id, @to_id)
            order by id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("from_id", fromMembershipId);
        command.Parameters.AddWithValue("to_id", toMembershipId);

        var rows = new List<OwnershipTransferRow>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OwnershipTransferRow(
                reader.GetGuid(reader.GetOrdinal("id")),
                UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetBoolean(reader.GetOrdinal("is_owner"))));
        }

        return rows;
    }

    private static async Task InsertOwnershipTransferAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        OwnershipTransferRow fromRow,
        OwnershipTransferRow toRow,
        UserId? sysAdminAccountId,
        string reason,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            CompanyId = companyId,
            FromMembershipId = fromRow.MembershipId,
            FromUserId = fromRow.UserId,
            ToMembershipId = toRow.MembershipId,
            ToUserId = toRow.UserId,
            Reason = reason,
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
              'ownership_transferred',
              @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue(
            "actor_id",
            sysAdminAccountId.HasValue ? (object)sysAdminAccountId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", toRow.MembershipId.ToString("D"));
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record OwnershipTransferRow(
        Guid MembershipId,
        UserId UserId,
        bool IsActive,
        bool IsOwner);
}
