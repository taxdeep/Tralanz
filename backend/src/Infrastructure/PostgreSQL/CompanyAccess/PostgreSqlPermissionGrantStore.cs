using System.Text.Json;
using Modules.CompanyAccess.Permissions;
using Npgsql;

namespace Infrastructure.PostgreSQL.CompanyAccess;

/// <summary>
/// PostgreSQL-backed <see cref="IPermissionGrantStore"/>. Writes
/// company_user_permissions + audit_logs in a single transaction so
/// the grant state and audit trail can never diverge.
/// </summary>
public sealed class PostgreSqlPermissionGrantStore(PostgreSqlConnectionFactory connections)
    : IPermissionGrantStore
{
    public async Task<bool> InsertGrantAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The partial unique index on (company, user, token) WHERE
        // is_active=true makes "is there already an active row" a
        // single index probe. We could use INSERT ... ON CONFLICT DO
        // NOTHING but then we lose the affected-row signal needed to
        // decide whether to write an audit row. Explicit branch keeps
        // the audit log free of duplicate "granted same token twice"
        // entries.
        bool alreadyActive;
        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText =
                """
                select exists(
                  select 1
                    from company_user_permissions
                   where company_id = @company_id
                     and user_id = @target_user_id
                     and permission_token = @token
                     and is_active = true
                );
                """;
            existsCommand.Parameters.AddWithValue("company_id", companyId.Value);
            existsCommand.Parameters.AddWithValue("target_user_id", targetUserId.Value);
            existsCommand.Parameters.AddWithValue("token", permissionToken);
            alreadyActive = (bool)(await existsCommand.ExecuteScalarAsync(cancellationToken) ?? false);
        }

        if (alreadyActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into company_user_permissions
                  (company_id, user_id, permission_token, granted_by_user_id, granted_at, is_active)
                values
                  (@company_id, @target_user_id, @token, @actor_user_id, now(), true);
                """;
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("target_user_id", targetUserId.Value);
            insertCommand.Parameters.AddWithValue("token", permissionToken);
            insertCommand.Parameters.AddWithValue("actor_user_id", actorUserId.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection, transaction, companyId, actorUserId, targetUserId,
            permissionToken, action: "permission_granted", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkRevokedAsync(
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int rowsAffected;
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update company_user_permissions
                   set is_active = false,
                       revoked_by_user_id = @actor_user_id,
                       revoked_at = now()
                 where company_id = @company_id
                   and user_id = @target_user_id
                   and permission_token = @token
                   and is_active = true;
                """;
            updateCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateCommand.Parameters.AddWithValue("target_user_id", targetUserId.Value);
            updateCommand.Parameters.AddWithValue("token", permissionToken);
            updateCommand.Parameters.AddWithValue("actor_user_id", actorUserId.Value);
            rowsAffected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (rowsAffected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await InsertAuditAsync(
            connection, transaction, companyId, actorUserId, targetUserId,
            permissionToken, action: "permission_revoked", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<UserPermissionSnapshot> ReadUserPermissionsAsync(
        CompanyId companyId,
        UserId targetUserId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // Owner flag is the cheapest lookup; do it first so the
        // caller can render "Owner — has implied-all" without
        // pulling thousands of grant rows.
        bool isOwner;
        await using (var ownerCommand = connection.CreateCommand())
        {
            ownerCommand.CommandText =
                """
                select coalesce(
                  (select is_owner from company_memberships
                    where company_id = @company_id and user_id = @user_id
                      and status = 'active'),
                  false);
                """;
            ownerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            ownerCommand.Parameters.AddWithValue("user_id", targetUserId.Value);
            isOwner = (bool)(await ownerCommand.ExecuteScalarAsync(cancellationToken) ?? false);
        }

        var grants = new List<PermissionGrant>();
        await using (var grantsCommand = connection.CreateCommand())
        {
            grantsCommand.CommandText =
                """
                select permission_token, granted_by_user_id, granted_at,
                       revoked_by_user_id, revoked_at, is_active
                  from company_user_permissions
                 where company_id = @company_id
                   and user_id = @user_id
                   and is_active = true
                 order by permission_token asc;
                """;
            grantsCommand.Parameters.AddWithValue("company_id", companyId.Value);
            grantsCommand.Parameters.AddWithValue("user_id", targetUserId.Value);
            await using var reader = await grantsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                grants.Add(new PermissionGrant
                {
                    CompanyId = companyId,
                    UserId = targetUserId,
                    PermissionToken = reader.GetString(0),
                    GrantedByUserId = UserId.Parse(reader.GetString(1)),
                    GrantedAtUtc = reader.GetFieldValue<DateTimeOffset>(2),
                    RevokedByUserId = reader.IsDBNull(3) ? null : UserId.Parse(reader.GetString(3)),
                    RevokedAtUtc = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    IsActive = reader.GetBoolean(5),
                });
            }
        }

        var authorities = new List<PermissionGrantAuthority>();
        await using (var authCommand = connection.CreateCommand())
        {
            authCommand.CommandText =
                """
                select grantable_permission_token, can_grant, can_revoke,
                       granted_by_owner_user_id, granted_at,
                       revoked_by_owner_user_id, revoked_at, is_active
                  from company_user_permission_grant_authorities
                 where company_id = @company_id
                   and user_id = @user_id
                   and is_active = true
                 order by grantable_permission_token asc;
                """;
            authCommand.Parameters.AddWithValue("company_id", companyId.Value);
            authCommand.Parameters.AddWithValue("user_id", targetUserId.Value);
            await using var reader = await authCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                authorities.Add(new PermissionGrantAuthority
                {
                    CompanyId = companyId,
                    UserId = targetUserId,
                    GrantablePermissionToken = reader.GetString(0),
                    CanGrant = reader.GetBoolean(1),
                    CanRevoke = reader.GetBoolean(2),
                    GrantedByOwnerUserId = UserId.Parse(reader.GetString(3)),
                    GrantedAtUtc = reader.GetFieldValue<DateTimeOffset>(4),
                    RevokedByOwnerUserId = reader.IsDBNull(5) ? null : UserId.Parse(reader.GetString(5)),
                    RevokedAtUtc = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    IsActive = reader.GetBoolean(7),
                });
            }
        }

        return new UserPermissionSnapshot
        {
            CompanyId = companyId,
            UserId = targetUserId,
            IsOwner = isOwner,
            ActiveGrants = grants,
            ActiveGrantAuthorities = authorities,
        };
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId actorUserId,
        UserId targetUserId,
        string permissionToken,
        string action,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            CompanyId = companyId,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            PermissionToken = permissionToken,
        });

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into audit_logs (
              id, company_id, actor_type, actor_id,
              entity_type, entity_id, action, payload
            )
            values (
              @id, @company_id, 'business_user', @actor_id,
              'company_user_permission', @entity_id, @action, @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_id", actorUserId.Value);
        // entity_id is "<targetUserId>:<permissionToken>" so audit
        // search by entity finds every event for that grant target.
        command.Parameters.AddWithValue("entity_id", $"{targetUserId.Value}:{permissionToken}");
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
