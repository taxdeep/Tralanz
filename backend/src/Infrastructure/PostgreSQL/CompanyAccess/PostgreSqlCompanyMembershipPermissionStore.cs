using System.Text.Json;
using Modules.CompanyAccess.Memberships;
using Npgsql;

namespace Infrastructure.PostgreSQL.CompanyAccess;

public sealed class PostgreSqlCompanyMembershipPermissionStore : ICompanyMembershipPermissionStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private int _schemaEnsured;

    public PostgreSqlCompanyMembershipPermissionStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        // Cheap intra-process guard: the only call site is app startup,
        // so an in-memory flag is enough to avoid re-scanning every
        // membership on a second EnsureSchemaAsync from another caller.
        if (Volatile.Read(ref _schemaEnsured) == 1)
        {
            return;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Batch-3.5 schema: surface owner-ship as an explicit
        // is_owner flag (not a role string match), with a partial
        // unique index that guarantees at most one active owner per
        // company at any time. Idempotent — re-run is a no-op.
        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText =
                """
                alter table company_memberships
                  add column if not exists is_owner boolean not null default false;

                create unique index if not exists uq_company_memberships_one_owner
                  on company_memberships (company_id)
                  where is_owner = true;

                -- Backfill: every membership currently flagged as
                -- role='owner' becomes is_owner=true. Safe to run
                -- repeatedly — the where-clause skips rows already
                -- aligned, so subsequent boots write nothing.
                update company_memberships
                set is_owner = true,
                    updated_at = now()
                where role = 'owner'
                  and is_owner = false;
                """;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Stream every membership's current permissions and, for any
        // row whose legacy tokens haven't been expanded yet, write
        // back the expanded set. No batch SQL because the legacy →
        // fine-grained mapping lives in C# and is the single source
        // of truth; doing it in the language keeps drift impossible.
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.CommandText =
                """
                select id, company_id, permissions::text as permissions
                from company_memberships
                where permissions is not null;
                """;

            var pending = new List<(Guid Id, string CompanyId, string Json)>();
            await using (var reader = await readCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(reader.GetOrdinal("id"));
                    var companyId = reader.GetString(reader.GetOrdinal("company_id"));
                    var json = reader.GetString(reader.GetOrdinal("permissions"));
                    var current = ParsePermissionTokens(json);

                    if (!CompanyMembershipPermissionLegacyExpansion.NeedsExpansion(current))
                    {
                        continue;
                    }

                    var expanded = CompanyMembershipPermissionLegacyExpansion.Expand(current);
                    pending.Add((id, companyId, JsonSerializer.Serialize(expanded)));
                }
            }

            foreach (var (id, companyId, json) in pending)
            {
                await using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText =
                    """
                    update company_memberships
                    set permissions = @permissions::jsonb,
                        updated_at = now()
                    where company_id = @company_id
                      and id = @membership_id;
                    """;
                updateCommand.Parameters.AddWithValue("company_id", companyId);
                updateCommand.Parameters.AddWithValue("membership_id", id);
                updateCommand.Parameters.AddWithValue("permissions", json);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        Volatile.Write(ref _schemaEnsured, 1);
    }

    public async Task<IReadOnlyList<CompanyMembershipPermissionListItem>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

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
              m.is_owner,
              m.updated_at
            from company_memberships m
            inner join users u on u.id = m.user_id
            where m.company_id = @company_id
            order by
              case when m.is_owner then 0 else 1 end,
              u.email,
              u.username;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var memberships = new List<CompanyMembershipPermissionListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memberships.Add(ReadMembership(reader));
        }

        return memberships;
    }

    public async Task<IReadOnlyList<CompanyMembershipPermissionAuditRecord>> ListRecentAuditAsync(
        CompanyId companyId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              al.id,
              al.company_id,
              al.actor_id,
              actor.email as actor_email,
              actor.username as actor_username,
              al.entity_id::uuid as membership_id,
              target_user.id as target_user_id,
              target_user.email as target_email,
              target_user.username as target_username,
              al.payload::text as payload,
              al.created_at
            from audit_logs al
            left join users actor on actor.id = al.actor_id
            left join company_memberships target_membership
              on target_membership.id::text = al.entity_id
             and target_membership.company_id = al.company_id
            left join users target_user on target_user.id = target_membership.user_id
            where al.company_id = @company_id
              and al.entity_type = 'company_membership'
              and al.action = 'membership_permissions_saved'
            order by al.created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
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
        CompanyId companyId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

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
              m.is_owner,
              m.updated_at
            from company_memberships m
            inner join users u on u.id = m.user_id
            where m.company_id = @company_id
              and m.id = @membership_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("membership_id", membershipId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMembership(reader)
            : null;
    }

    public async Task<CompanyMembershipPermissionActorAuthority?> GetActorAuthorityAsync(
        CompanyId companyId,
        UserId actorUserId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_user_id", actorUserId.Value);

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

    public Task<CompanyMembershipPermissionListItem?> SavePermissionsAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId actorUserId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken) =>
        PersistPermissionsAsync(
            companyId,
            membershipId,
            actorUserId,
            actorType: "user",
            permissionTokens,
            cancellationToken);

    public Task<CompanyMembershipPermissionListItem?> SavePermissionsFromSysAdminAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId? sysAdminAccountId,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken) =>
        PersistPermissionsAsync(
            companyId,
            membershipId,
            sysAdminAccountId,
            actorType: "sysadmin",
            permissionTokens,
            cancellationToken);

    private async Task<CompanyMembershipPermissionListItem?> PersistPermissionsAsync(
        CompanyId companyId,
        Guid membershipId,
        UserId? actorUserId,
        string actorType,
        IReadOnlyList<string> permissionTokens,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        IReadOnlyList<string> previousTokens;
        UserId targetUserId;
        string targetRole;
        bool targetIsOwner;
        await using (var previousCommand = connection.CreateCommand())
        {
            previousCommand.Transaction = transaction;
            previousCommand.CommandText =
                """
                select user_id, role, permissions::text as permissions, is_owner
                from company_memberships
                where company_id = @company_id
                  and id = @membership_id
                for update;
                """;
            previousCommand.Parameters.AddWithValue("company_id", companyId.Value);
            previousCommand.Parameters.AddWithValue("membership_id", membershipId);

            await using var previousReader = await previousCommand.ExecuteReaderAsync(cancellationToken);
            if (!await previousReader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            targetUserId = UserId.Parse(previousReader.GetString(previousReader.GetOrdinal("user_id")));
            targetRole = previousReader.GetString(previousReader.GetOrdinal("role")).Trim().ToLowerInvariant();
            previousTokens = ParsePermissionTokens(previousReader.GetString(previousReader.GetOrdinal("permissions")));
            targetIsOwner = previousReader.GetBoolean(previousReader.GetOrdinal("is_owner"));
        }

        // Owner permissions are governance-locked: the owner always
        // holds the full catalog (see CompanyMembershipPermissionPresets.Owner)
        // and any direct edit is rejected. To change who has owner
        // power, callers must use the transfer-ownership pathway,
        // which atomically swaps is_owner and reassigns permissions.
        if (targetIsOwner)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "Owner permissions are immutable. Transfer ownership before changing this membership's permissions.");
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
              is_owner,
              updated_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("permissions", JsonSerializer.Serialize(permissionTokens));

        Guid? savedMembershipId = null;
        CompanyId? savedCompanyId = null;
        UserId? savedUserId = null;
        string? savedRole = null;
        string? savedPermissions = null;
        bool savedIsActive = false;
        bool savedIsOwner = false;
        DateTimeOffset savedUpdatedAt = DateTimeOffset.UtcNow;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            savedMembershipId = reader.GetGuid(reader.GetOrdinal("id"));
            savedCompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id")));
            savedUserId = UserId.Parse(reader.GetString(reader.GetOrdinal("user_id")));
            savedRole = reader.GetString(reader.GetOrdinal("role"));
            savedPermissions = reader.GetString(reader.GetOrdinal("permissions"));
            savedIsActive = reader.GetBoolean(reader.GetOrdinal("is_active"));
            savedIsOwner = reader.GetBoolean(reader.GetOrdinal("is_owner"));
            savedUpdatedAt = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("updated_at")));
        }

        await InsertAuditLogAsync(
            connection,
            transaction,
            companyId,
            membershipId,
            actorType,
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
        userCommand.Parameters.AddWithValue("user_id", savedUserId.Value.Value);

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
            IsOwner = savedIsOwner,
            UpdatedAt = savedUpdatedAt
        };
    }

    private static async Task InsertAuditLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid membershipId,
        string actorType,
        UserId? actorUserId,
        UserId targetUserId,
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
              @actor_type,
              @actor_id,
              'company_membership',
              @entity_id,
              'membership_permissions_saved',
              @payload::jsonb
            );
            """;
        auditCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        auditCommand.Parameters.AddWithValue("company_id", companyId.Value);
        auditCommand.Parameters.AddWithValue("actor_type", actorType);
        auditCommand.Parameters.AddWithValue(
            "actor_id",
            actorUserId.HasValue ? (object)actorUserId.Value.Value : DBNull.Value);
        auditCommand.Parameters.AddWithValue("entity_id", membershipId.ToString("D"));
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
            CompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            MembershipId = reader.GetGuid(reader.GetOrdinal("membership_id")),
            ActorUserId = reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            ActorEmail = actorEmail,
            ActorDisplayName = !string.IsNullOrWhiteSpace(actorUsername) ? actorUsername : actorEmail,
            TargetUserId = reader.IsDBNull(reader.GetOrdinal("target_user_id"))
                ? TryReadUserId(root, "TargetUserId")
                : UserId.Parse(reader.GetString(reader.GetOrdinal("target_user_id"))),
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
            CompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            UserId = UserId.Parse(reader.GetString(reader.GetOrdinal("user_id"))),
            Email = email,
            Username = username,
            DisplayName = !string.IsNullOrWhiteSpace(username) ? username : email,
            Role = reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant(),
            PermissionTokens = ParsePermissionTokens(reader.GetString(reader.GetOrdinal("permissions"))),
            IsActive = reader.GetBoolean(reader.GetOrdinal("is_active")),
            IsOwner = reader.GetBoolean(reader.GetOrdinal("is_owner")),
            UpdatedAt = CoerceTimestamp(reader.GetValue(reader.GetOrdinal("updated_at")))
        };
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

    private static UserId? TryReadUserId(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !UserId.TryParse(value.GetString(), out var parsed))
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
