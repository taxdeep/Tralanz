using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modules.Company.FeatureManagement;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Company;

public sealed class PostgreSqlCompanyModuleFlagStore(
    PostgreSqlConnectionFactory connections) : ICompanyModuleFlagStore
{
    private const string SchemaSql =
        """
        create table if not exists company_module_flags (
          company_id   char(7)      not null,
          module_key   varchar(64)  not null,
          enabled      boolean      not null,
          access_expires_at timestamptz null,
          updated_at   timestamptz  not null default now(),
          updated_by   char(7)      null,
          primary key (company_id, module_key)
        );
        create index if not exists ix_company_module_flags_company
          on company_module_flags (company_id);
        """;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select module_key, enabled, access_expires_at, updated_at, updated_by
            from company_module_flags
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var persisted = new Dictionary<string, PersistedRow>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(reader.GetOrdinal("module_key")).Trim();
                var enabled = reader.GetBoolean(reader.GetOrdinal("enabled"));
                var accessExpiresAtOrdinal = reader.GetOrdinal("access_expires_at");
                var accessExpiresAt = reader.IsDBNull(accessExpiresAtOrdinal)
                    ? (DateTimeOffset?)null
                    : ToUtcOffset(reader.GetFieldValue<DateTime>(accessExpiresAtOrdinal));
                var updatedAt = reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at"));
                var updatedByOrdinal = reader.GetOrdinal("updated_by");
                UserId? updatedBy = reader.IsDBNull(updatedByOrdinal)
                    ? null
                    : UserId.Parse(reader.GetString(updatedByOrdinal).Trim());
                persisted[key] = new PersistedRow(
                    enabled,
                    accessExpiresAt,
                    ToUtcOffset(updatedAt),
                    updatedBy);
            }
        }

        // The catalog drives the visible list — DB rows merely fill in
        // state. A module key removed from the catalog stops being
        // returned even if its row still exists; that's intentional so
        // a retired key isn't surfaced as toggleable in the UI.
        var summaries = new List<CompanyModuleFlagSummary>(CompanyModuleFlagCatalog.Options.Count);
        foreach (var option in CompanyModuleFlagCatalog.Options)
        {
            if (persisted.TryGetValue(option.Key, out var row))
            {
                summaries.Add(new CompanyModuleFlagSummary(
                    companyId,
                    option.Key,
                    option.DisplayName,
                    option.Description,
                    row.Enabled,
                    row.AccessExpiresAtUtc,
                    row.Enabled && row.AccessExpiresAtUtc.HasValue && row.AccessExpiresAtUtc.Value <= DateTimeOffset.UtcNow,
                    row.UpdatedAtUtc,
                    row.UpdatedBy));
            }
            else
            {
                summaries.Add(new CompanyModuleFlagSummary(
                    companyId,
                    option.Key,
                    option.DisplayName,
                    option.Description,
                    Enabled: false,
                    AccessExpiresAtUtc: null,
                    IsExpired: false,
                    UpdatedAtUtc: null,
                    UpdatedByUserId: null));
            }
        }

        return summaries;
    }

    public async Task<bool> IsEnabledAsync(
        CompanyId companyId,
        string moduleKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select enabled
            from company_module_flags
            where company_id = @company_id
              and module_key = @module_key;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("module_key", moduleKey);

        var raw = await command.ExecuteScalarAsync(cancellationToken);
        return raw is bool enabled && enabled;
    }

    public async Task<CompanyModuleFlagAccessStatus> GetAccessStatusAsync(
        CompanyId companyId,
        string moduleKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select enabled, access_expires_at
            from company_module_flags
            where company_id = @company_id
              and module_key = @module_key;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("module_key", moduleKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CompanyModuleFlagAccessStatus(
                companyId,
                moduleKey,
                Enabled: false,
                AccessExpiresAtUtc: null,
                IsExpired: false);
        }

        var enabled = reader.GetBoolean(reader.GetOrdinal("enabled"));
        var accessExpiresAtOrdinal = reader.GetOrdinal("access_expires_at");
        var accessExpiresAt = reader.IsDBNull(accessExpiresAtOrdinal)
            ? (DateTimeOffset?)null
            : ToUtcOffset(reader.GetFieldValue<DateTime>(accessExpiresAtOrdinal));

        return new CompanyModuleFlagAccessStatus(
            companyId,
            moduleKey,
            enabled,
            accessExpiresAt,
            enabled && accessExpiresAt.HasValue && accessExpiresAt.Value <= nowUtc);
    }

    public async Task<CompanyModuleFlagUpdateResult> SetEnabledAsync(
        CompanyId companyId,
        string moduleKey,
        bool enabled,
        string reason,
        string actorType,
        UserId? actorUserId,
        DateTimeOffset? accessExpiresAtUtc,
        bool forceAuditOnNoChange,
        CancellationToken cancellationToken)
    {
        var option = CompanyModuleFlagCatalog.Options.First(
            o => string.Equals(o.Key, moduleKey, StringComparison.Ordinal));

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Read the previous state (row-lock) so the audit row carries
        // an honest before/after snapshot even when the toggle is a
        // no-op. Locking serializes concurrent writers — last writer
        // wins, with both writes producing audit rows.
        var previous = await ReadForUpdateAsync(connection, transaction, companyId, moduleKey, cancellationToken);
        var previouslyEnabled = previous?.Enabled ?? false;
        var changed = previouslyEnabled != enabled;

        DateTimeOffset updatedAt;
        await using (var upsertCommand = connection.CreateCommand())
        {
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText =
                """
                insert into company_module_flags (company_id, module_key, enabled, access_expires_at, updated_at, updated_by)
                values (@company_id, @module_key, @enabled, @access_expires_at, now(), @updated_by)
                on conflict (company_id, module_key) do update set
                  enabled = excluded.enabled,
                  access_expires_at = excluded.access_expires_at,
                  updated_at = excluded.updated_at,
                  updated_by = excluded.updated_by
                returning updated_at;
                """;
            upsertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            upsertCommand.Parameters.AddWithValue("module_key", moduleKey);
            upsertCommand.Parameters.AddWithValue("enabled", enabled);
            upsertCommand.Parameters.AddWithValue(
                "access_expires_at",
                accessExpiresAtUtc.HasValue ? (object)accessExpiresAtUtc.Value : DBNull.Value);
            upsertCommand.Parameters.AddWithValue(
                "updated_by",
                actorUserId.HasValue ? (object)actorUserId.Value.Value : DBNull.Value);

            var raw = await upsertCommand.ExecuteScalarAsync(cancellationToken);
            var rawUtc = DateTime.SpecifyKind((DateTime)raw!, DateTimeKind.Utc);
            updatedAt = new DateTimeOffset(rawUtc, TimeSpan.Zero);
        }

        if (changed || forceAuditOnNoChange)
        {
            await InsertAuditLogAsync(
                connection,
                transaction,
                companyId,
                moduleKey,
                actorType,
                actorUserId,
                previouslyEnabled,
                enabled,
                reason,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var summary = new CompanyModuleFlagSummary(
            companyId,
            moduleKey,
            option.DisplayName,
            option.Description,
            enabled,
            accessExpiresAtUtc,
            enabled && accessExpiresAtUtc.HasValue && accessExpiresAtUtc.Value <= DateTimeOffset.UtcNow,
            updatedAt,
            actorUserId);

        return new CompanyModuleFlagUpdateResult(summary, changed, reason);
    }

    private static async Task<PreviousRow?> ReadForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string moduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select enabled
            from company_module_flags
            where company_id = @company_id
              and module_key = @module_key
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("module_key", moduleKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PreviousRow(reader.GetBoolean(reader.GetOrdinal("enabled")));
    }

    private static async Task InsertAuditLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string moduleKey,
        string actorType,
        UserId? actorUserId,
        bool previouslyEnabled,
        bool enabled,
        string reason,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            CompanyId = companyId.Value,
            ModuleKey = moduleKey,
            PreviouslyEnabled = previouslyEnabled,
            Enabled = enabled,
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
              @actor_type,
              @actor_id,
              'company_module_flag',
              @entity_id,
              @action,
              @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_type", actorType);
        command.Parameters.AddWithValue(
            "actor_id",
            actorUserId.HasValue ? (object)actorUserId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", BuildEntityId(companyId, moduleKey));
        command.Parameters.AddWithValue("action", enabled ? "module_enabled" : "module_disabled");
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // audit_logs.entity_id is uuid NOT NULL but the company_module_flag
    // entity is naturally identified by (company_id, module_key). Hash
    // the composite to a stable Guid so "show every audit row for this
    // flag" remains a single-column query — and the value is the same
    // every time the same flag changes, the way membership_id stays
    // the same across role-change events.
    private static Guid BuildEntityId(CompanyId companyId, string moduleKey)
    {
        var seed = $"company_module_flag:{companyId.Value}:{moduleKey}";
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash);
    }

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private sealed record PersistedRow(
        bool Enabled,
        DateTimeOffset? AccessExpiresAtUtc,
        DateTimeOffset UpdatedAtUtc,
        UserId? UpdatedBy);

    private sealed record PreviousRow(bool Enabled);
}
