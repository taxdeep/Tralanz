using System.Text.Json;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.SessionContext;
using Npgsql;
using SharedKernel.CompanyAccess;

namespace Infrastructure.PostgreSQL.CompanyAccess;

public sealed class PostgreSqlCompanySessionContextStore : ICompanySessionContextStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlCompanySessionContextStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<CompanyAccessSessionContext?> GetAsync(
        UserId userId,
        CompanyId? preferredActiveCompanyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        var user = await ReadUserAsync(connection, userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var companies = await ReadCompaniesAsync(connection, userId, cancellationToken);
        if (companies.Count == 0)
        {
            return null;
        }

        var activeCompany = ResolveActiveCompany(preferredActiveCompanyId, companies);
        return new CompanyAccessSessionContext
        {
            User = user with
            {
                Roles = BuildSessionRoles(activeCompany),
            },
            ActiveCompany = ToSummary(activeCompany),
            AvailableCompanies = companies.Select(ToSummary).ToArray()
        };
    }

    /// <summary>
    /// Owner safety net (Batch 3.6): if the membership is flagged
    /// <c>is_owner=true</c>, append every catalog token to the
    /// session-side Roles list, regardless of what's persisted on the
    /// membership. Owner permissions are governance-locked at write
    /// time (the Postgres permission store rejects edits), but the
    /// catalog can grow between the moment ownership was assigned and
    /// the moment a request comes in — this Union guarantees the owner
    /// always sees the current catalog without a separate
    /// reconciliation job.
    /// </summary>
    private static IReadOnlyList<string> BuildSessionRoles(CompanyMembershipCompanyRecord activeCompany)
    {
        IEnumerable<string> baseRoles = activeCompany.PermissionTokens
            .Prepend(activeCompany.MembershipRole)
            .Where(static role => !string.IsNullOrWhiteSpace(role));

        if (activeCompany.IsOwner)
        {
            baseRoles = baseRoles.Concat(CompanyMembershipPermissionCatalog.AllTokens);
        }

        return baseRoles
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static role => role, StringComparer.Ordinal)
            .ToArray();
    }

    private static CompanyAccessCompanySummary ToSummary(CompanyMembershipCompanyRecord company) =>
        new()
        {
            Id = company.Id,
            CompanyCode = company.CompanyCode,
            CompanyName = company.CompanyName,
            BaseCurrencyCode = company.BaseCurrencyCode,
            MultiCurrencyEnabled = company.MultiCurrencyEnabled,
            InventoryModuleEnabled = company.InventoryModuleEnabled,
            Status = company.Status,
            IsReadOnly = !string.Equals(company.Status, "active", StringComparison.Ordinal)
        };

    private static CompanyMembershipCompanyRecord ResolveActiveCompany(
        CompanyId? preferredActiveCompanyId,
        IReadOnlyList<CompanyMembershipCompanyRecord> companies)
    {
        if (preferredActiveCompanyId.HasValue)
        {
            var configured = companies.FirstOrDefault(company => company.Id == preferredActiveCompanyId.Value);
            if (configured is not null)
            {
                return configured;
            }
        }

        return companies.FirstOrDefault(static company => string.Equals(company.Status, "active", StringComparison.Ordinal))
            ?? companies[0];
    }

    private static async Task<CompanyAccessUserSummary?> ReadUserAsync(
        NpgsqlConnection connection,
        UserId userId,
        CancellationToken cancellationToken)
    {
        var hasStatusColumn = await HasColumnAsync(connection, "users", "status", cancellationToken);
        var hasLockedUntilColumn = await HasColumnAsync(connection, "users", "locked_until", cancellationToken);
        var hasDisplayNameColumn = await HasColumnAsync(connection, "users", "display_name", cancellationToken);

        await using var command = connection.CreateCommand();
        var displayNameProjection = hasDisplayNameColumn
            ? "display_name"
            : "null::text as display_name";
        var accountStatusPredicate = hasStatusColumn
            ? hasLockedUntilColumn
                ? "status = 'active' and (locked_until is null or locked_until <= now())"
                : "status = 'active'"
            : "is_active = true";

        command.CommandText =
            $"""
            select id, email, username, {displayNameProjection}
            from users
            where id = @user_id
              and {accountStatusPredicate}
            limit 1;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var email = reader.GetString(reader.GetOrdinal("email")).Trim();
        var username = reader.IsDBNull(reader.GetOrdinal("username"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("username")).Trim();
        var displayName = reader.IsDBNull(reader.GetOrdinal("display_name"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("display_name")).Trim();

        return new CompanyAccessUserSummary
        {
            Id = UserId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            Email = email,
            Username = username,
            DisplayName = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : !string.IsNullOrWhiteSpace(username) ? username : email
        };
    }

    private static async Task<IReadOnlyList<CompanyMembershipCompanyRecord>> ReadCompaniesAsync(
        NpgsqlConnection connection,
        UserId userId,
        CancellationToken cancellationToken)
    {
        var hasPermissionsColumn = await HasMembershipPermissionsColumnAsync(connection, cancellationToken);
        // Defensive: the inventory_module_enabled column is added by the
        // platform-provisioning ALTER. Older Tralanz Books deployments
        // that haven't yet run the bumped startup will lack the column;
        // fall back to false there so the session still resolves.
        var hasInventoryModuleColumn = await HasColumnAsync(connection, "companies", "inventory_module_enabled", cancellationToken);
        var inventoryModuleSelect = hasInventoryModuleColumn
            ? "c.inventory_module_enabled"
            : "false as inventory_module_enabled";
        // Same defensive pattern: is_owner is added by the Batch-3.5
        // membership-permission EnsureSchemaAsync. If a session
        // resolves before that ALTER ran, fall back to false — the
        // worst case is the owner doesn't get the catalog Union for
        // a brief window, which is the same as pre-3.6 behavior.
        var hasIsOwnerColumn = await HasColumnAsync(connection, "company_memberships", "is_owner", cancellationToken);
        var isOwnerSelect = hasIsOwnerColumn
            ? "m.is_owner"
            : "false as is_owner";

        await using var command = connection.CreateCommand();
        command.CommandText = hasPermissionsColumn
            ?
            $"""
            select
              c.id,
              c.entity_number,
              c.legal_name,
              c.base_currency_code,
              c.multi_currency_enabled,
              {inventoryModuleSelect},
              c.status,
              m.role,
              {isOwnerSelect},
              m.permissions::text as permissions
            from company_memberships m
            inner join companies c on c.id = m.company_id
            where m.user_id = @user_id
              and m.is_active = true
              and c.status in ('active', 'inactive')
            order by c.entity_number, c.legal_name;
            """
            :
            $"""
            select
              c.id,
              c.entity_number,
              c.legal_name,
              c.base_currency_code,
              c.multi_currency_enabled,
              {inventoryModuleSelect},
              c.status,
              m.role,
              {isOwnerSelect},
              null::text as permissions
            from company_memberships m
            inner join companies c on c.id = m.company_id
            where m.user_id = @user_id
              and m.is_active = true
              and c.status in ('active', 'inactive')
            order by c.entity_number, c.legal_name;
            """;
        command.Parameters.AddWithValue("user_id", userId.Value);

        var companies = new List<CompanyMembershipCompanyRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            companies.Add(
                new CompanyMembershipCompanyRecord(
                    CompanyId.Parse(reader.GetString(reader.GetOrdinal("id"))),
                    reader.GetString(reader.GetOrdinal("entity_number")).Trim().ToUpperInvariant(),
                    reader.GetString(reader.GetOrdinal("legal_name")).Trim(),
                    reader.GetString(reader.GetOrdinal("base_currency_code")).Trim().ToUpperInvariant(),
                    reader.GetBoolean(reader.GetOrdinal("multi_currency_enabled")),
                    reader.GetBoolean(reader.GetOrdinal("inventory_module_enabled")),
                    reader.GetString(reader.GetOrdinal("status")).Trim().ToLowerInvariant(),
                    reader.GetString(reader.GetOrdinal("role")).Trim().ToLowerInvariant(),
                    reader.GetBoolean(reader.GetOrdinal("is_owner")),
                    ParsePermissionTokens(reader.IsDBNull(reader.GetOrdinal("permissions"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("permissions")))));
        }

        return companies;
    }

    private static async Task<bool> HasMembershipPermissionsColumnAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
        => await HasColumnAsync(connection, "company_memberships", "permissions", cancellationToken);

    private static async Task<bool> HasColumnAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists (
              select 1
              from information_schema.columns
              where table_schema = 'public'
                and table_name = @table_name
                and column_name = @column_name
            );
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_name", columnName);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
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
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement
                    .EnumerateArray()
                    .Select(TryReadPermissionToken)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static token => token, StringComparer.Ordinal)
                    .ToArray(),
                JsonValueKind.Object => ReadObjectPermissionTokens(document.RootElement),
                JsonValueKind.String => NormalizePermissionToken(document.RootElement.GetString()),
                _ => Array.Empty<string>()
            };
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ReadObjectPermissionTokens(JsonElement element)
    {
        var permissions = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.True)
            {
                AddPermissionToken(permissions, property.Name);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                AddPermissionToken(permissions, property.Value.GetString());
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                AddPermissionToken(permissions, TryReadPermissionToken(item));
            }
        }

        return permissions
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? TryReadPermissionToken(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? NormalizePermissionToken(element.GetString()).FirstOrDefault()
            : null;

    private static IReadOnlyList<string> NormalizePermissionToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return [value.Trim().ToLowerInvariant()];
    }

    private static void AddPermissionToken(List<string> permissions, string? value)
    {
        var normalized = NormalizePermissionToken(value);
        if (normalized.Count > 0)
        {
            permissions.Add(normalized[0]);
        }
    }

    private sealed record CompanyMembershipCompanyRecord(
        CompanyId Id,
        string CompanyCode,
        string CompanyName,
        string BaseCurrencyCode,
        bool MultiCurrencyEnabled,
        bool InventoryModuleEnabled,
        string Status,
        string MembershipRole,
        bool IsOwner,
        IReadOnlyList<string> PermissionTokens);
}
