using System.Text.Json;
using Modules.CompanyAccess.Memberships;
using Npgsql;

namespace Infrastructure.PostgreSQL.CompanyAccess;

public sealed class PostgreSqlCompanyMembershipPermissionStore : ICompanyMembershipPermissionStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlCompanyMembershipPermissionStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsurePermissionsColumnAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              m.id,
              m.company_id,
              m.user_id,
              u.email,
              u.username,
              m.role,
              m.permissions::text as permissions,
              m.is_active,
              m.updated_at
            from company_memberships m
            inner join users u on u.id = m.user_id
            where m.company_id = @company_id
            order by
              case when m.role = 'owner' then 0 else 1 end,
              u.email,
              u.username;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var memberships = new List<CompanyMembershipPermissionListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memberships.Add(ReadMembership(reader));
        }

        return memberships;
    }

    public async Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        Guid companyId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureAuditLogsTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              al.id,
              al.company_id,
              al.actor_id,
              actor.email as actor_email,
              actor.username as actor_username,
              al.entity_id as membership_id,
              target_user.id as target_user_id,
              target_user.email as target_email,
              target_user.username as target_username,
              al.payload::text as payload,
              al.created_at
            from audit_logs al
            left join users actor on actor.id = al.actor_id
            left join company_memberships target_membership
              on target_membership.id = al.entity_id
             and target_membership.company_id = al.company_id
            left join users target_user on target_user.id = target_membership.user_id
            where al.company_id = @company_id
              and al.entity_type = 'company_membership'
              and al.action = 'membership_permissions_saved'
            order by al.created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("limit", limit);

        var records = new List<CompanyMembershipPermissionAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadAuditRecord(reader));
        }

        return records;
    }

    public async Task<CompanyMembershipPermissionListItem?> GetAsync(
        Guid companyId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsurePermissionsColumnAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              m.id,
              m.company_id,
              m.user_id,
              u.email,
              u.username,
              m.role,
              m.permissions::text as permissions,
              m.is_active,
              m.updated_at
            from company_memberships m
            inner join users u on u.id = m.user_id
            where m.company_id = @company_id
              and m.id = @membership_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("membership_id", membershipId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMembership(reader)
            : null;
    }

    public async Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
        Guid companyId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsurePermissionsColumnAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select role, permissions::text as permissions
            from company_memberships
            where company_id = @company_id
              and user_id = @actor_user_id
              and is_active = true
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("actor_user_id", actorUserId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompanyMembershipPermissionActorAuthority(
            companyId,
            actorUserId,
            reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant(),
            ParsePermissionTokens(reader.GetString(reader.GetOrdinal("permissions"))));
    }

    public async Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
        Guid companyId,
        Guid membershipId,
        Guid actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsurePermissionsColumnAsync(connection, cancellationToken);
        await EnsureAuditLogsTableAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        IReadOnlyList<string> previousTokens;
        Guid targetUserId;
        string targetRole;
        await using (var previousCommand = connection.CreateCommand())
        {
            previousCommand.Transaction = transaction;
            previousCommand.CommandText =
                """
                select user_id, role, permissions::text as permissions
                from company_memberships
                where company_id = @company_id
                  and id = @membership_id
                for update;
                """;
            previousCommand.Parameters.AddWithValue("company_id", companyId);
            previousCommand.Parameters.AddWithValue("membership_id", membershipId);

            await using var previousReader = await previousCommand.ExecuteReaderAsync(cancellationToken);
            if (!await previousReader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            targetUserId = previousReader.GetGuid(previousReader.GetOrdinal("user_id"));
            targetRole = previousReader.GetString(previousReader.GetOrdinal("role")).Trim().ToLowerInvariant();
            previousTokens = ParsePermissionTokens(previousReader.GetString(previousReader.GetOrdinal("permissions")));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update company_memberships
            set permissions = @permissions::jsonb,
                updated_at = now()
            where company_id = @company_id
              and id = @membership_id
            returning
              id,
              company_id,
              user_id,
              role,
              permissions::text as permissions,
              is_active,
              updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("permissions", JsonSerializer.Serialize(permissionTokens));

        Guid? savedMembershipId = null;
        Guid? savedCompanyId = null;
        Guid? savedUserId = null;
        string? savedRole = null;
        string? savedPermissions = null;
        bool savedIsActive = false;
        DateTimeOffset savedUpdatedAt = DateTimeOffset.UtcNow;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            savedMembershipId = reader.GetGuid(reader.GetOrdinal("id"));
            savedCompanyId = reader.GetGuid(reader.GetOrdinal("company_id"));
            savedUserId = reader.GetGuid(reader.GetOrdinal("user_id"));
            savedRole = reader.GetString(reader.GetOrdinal("role"));
            savedPermissions = reader.GetString(reader.GetOrdinal("permissions"));
            savedIsActive = reader.GetBoolean(reader.GetOrdinal("is_active"));
            savedUpdatedAt = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("updated_at")));
        }

        await InsertAuditLogAsync(
            connection,
            transaction,
            companyId,
            membershipId,
            actorUserId,
            targetUserId,
            targetRole,
            previousTokens,
            permissionTokens,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await using var userCommand = connection.CreateCommand();
        userCommand.CommandText =
            """
            select email, username
            from users
            where id = @user_id
            limit 1;
            """;
        userCommand.Parameters.AddWithValue("user_id", savedUserId.Value);

        await using var userReader = await userCommand.ExecuteReaderAsync(cancellationToken);
        if (!await userReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var email = userReader.GetString(userReader.GetOrdinal("email")).Trim();
        var username = userReader.IsDBNull(userReader.GetOrdinal("username"))
            ? string.Empty
            : userReader.GetString(userReader.GetOrdinal("username")).Trim();

        return new CompanyMembershipPermissionListItem
        {
            MembershipId = savedMembershipId.Value,
            CompanyId = savedCompanyId.Value,
            UserId = savedUserId.Value,
            Email = email,
            Username = username,
            DisplayName = !string.IsNullOrWhiteSpace(username) ? username : email,
            Role = savedRole!.Trim().ToLowerInvariant(),
            PermissionTokens = ParsePermissionTokens(savedPermissions),
            IsActive = savedIsActive,
            UpdatedAt = savedUpdatedAt
        };
    }

    private static async Task InsertAuditLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid membershipId,
        Guid actorUserId,
        Guid targetUserId,
        string targetRole,
        IReadOnlyList<string> previousTokens,
        IReadOnlyList<string> savedTokens,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                CompanyId = companyId,
                MembershipId = membershipId,
                TargetUserId = targetUserId,
                TargetRole = targetRole,
                PreviousPermissionTokens = previousTokens,
                SavedPermissionTokens = savedTokens,
                AddedPermissionTokens = savedTokens.Except(previousTokens, StringComparer.Ordinal).OrderBy(static token => token, StringComparer.Ordinal).ToArray(),
                RemovedPermissionTokens = previousTokens.Except(savedTokens, StringComparer.Ordinal).OrderBy(static token => token, StringComparer.Ordinal).ToArray()
            });

        await using var auditCommand = connection.CreateCommand();
        auditCommand.Transaction = transaction;
        auditCommand.CommandText =
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
              'user',
              @actor_id,
              'company_membership',
              @entity_id,
              'membership_permissions_saved',
              @payload::jsonb
            );
            """;
        auditCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        auditCommand.Parameters.AddWithValue("company_id", companyId);
        auditCommand.Parameters.AddWithValue("actor_id", actorUserId);
        auditCommand.Parameters.AddWithValue("entity_id", membershipId);
        auditCommand.Parameters.AddWithValue("payload", payload);
        await auditCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CompanyMembershipPermissionAuditRecord ReadAuditRecord(NpgsqlDataReader reader)
    {
        using var payload = JsonDocument.Parse(reader.GetString(reader.GetOrdinal("payload")));
        var root = payload.RootElement;
        var actorEmail = reader.IsDBNull(reader.GetOrdinal("actor_email"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("actor_email")).Trim();
        var actorUsername = reader.IsDBNull(reader.GetOrdinal("actor_username"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("actor_username")).Trim();
        var targetEmail = reader.IsDBNull(reader.GetOrdinal("target_email"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("target_email")).Trim();
        var targetUsername = reader.IsDBNull(reader.GetOrdinal("target_username"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("target_username")).Trim();

        return new CompanyMembershipPermissionAuditRecord
        {
            AuditId = reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId = reader.GetGuid(reader.GetOrdinal("company_id")),
            MembershipId = reader.GetGuid(reader.GetOrdinal("membership_id")),
            ActorUserId = reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : reader.GetGuid(reader.GetOrdinal("actor_id")),
            ActorEmail = actorEmail,
            ActorDisplayName = !string.IsNullOrWhiteSpace(actorUsername) ? actorUsername : actorEmail,
            TargetUserId = reader.IsDBNull(reader.GetOrdinal("target_user_id"))
                ? TryReadGuid(root, "TargetUserId")
                : reader.GetGuid(reader.GetOrdinal("target_user_id")),
            TargetEmail = targetEmail,
            TargetDisplayName = !string.IsNullOrWhiteSpace(targetUsername) ? targetUsername : targetEmail,
            TargetRole = ReadString(root, "TargetRole"),
            PreviousPermissionTokens = ReadTokens(root, "PreviousPermissionTokens"),
            SavedPermissionTokens = ReadTokens(root, "SavedPermissionTokens"),
            AddedPermissionTokens = ReadTokens(root, "AddedPermissionTokens"),
            RemovedPermissionTokens = ReadTokens(root, "RemovedPermissionTokens"),
            CreatedAt = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("created_at")))
        };
    }

    private static CompanyMembershipPermissionListItem ReadMembership(NpgsqlDataReader reader)
    {
        var email = reader.GetString(reader.GetOrdinal("email")).Trim();
        var username = reader.IsDBNull(reader.GetOrdinal("username"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("username")).Trim();

        return new CompanyMembershipPermissionListItem
        {
            MembershipId = reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId = reader.GetGuid(reader.GetOrdinal("company_id")),
            UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
            Email = email,
            Username = username,
            DisplayName = !string.IsNullOrWhiteSpace(username) ? username : email,
            Role = reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant(),
            PermissionTokens = ParsePermissionTokens(reader.GetString(reader.GetOrdinal("permissions"))),
            IsActive = reader.GetBoolean(reader.GetOrdinal("is_active")),
            UpdatedAt = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("updated_at")))
        };
    }

    private static async Task EnsurePermissionsColumnAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            alter table company_memberships
              add column if not exists permissions jsonb not null default '[]'::jsonb;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureAuditLogsTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<string> ParsePermissionTokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement
                .EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.String)
                .Select(static element => element.GetString())
                .Where(static token => !string.IsNullOrWhiteSpace(token))
                .Select(static token => token!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static token => token, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ReadTokens(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value
            .EnumerateArray()
            .Where(static element => element.ValueKind == JsonValueKind.String)
            .Select(static element => element.GetString())
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Select(static token => token!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;

    private static Guid? TryReadGuid(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static DateTimeOffset CoerceTimestamp(object? value)
    {
        if (value is DateTimeOffset offset)
        {
            return offset;
        }

        if (value is DateTime dateTime)
        {
            var normalized = dateTime.Kind == DateTimeKind.Utc
                ? dateTime
                : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
            return new DateTimeOffset(normalized);
        }

        return DateTimeOffset.UtcNow;
    }
}
