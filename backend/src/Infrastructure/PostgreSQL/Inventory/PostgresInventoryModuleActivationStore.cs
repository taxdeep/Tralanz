using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

/// <summary>
/// PostgreSQL adapter over the four <c>companies</c> columns that drive
/// Inventory-module activation lifecycle:
///   <c>inventory_module_enabled</c> / <c>_enabled_at</c> /
///   <c>_locked_at</c> / <c>inventory_profile_tag</c>.
///
/// All four were added by M1 (idempotent ALTER in the
/// platform-provisioning EnsureSchemaAsync). This store assumes they
/// exist; if a deployment hasn't restarted since M1 shipped, GetStateAsync
/// returns null defensively rather than crashing.
/// </summary>
public sealed class PostgresInventoryModuleActivationStore : IInventoryModuleActivationStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgresInventoryModuleActivationStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<InventoryModuleActivationStateRecord> MarkEnabledAsync(
        CompanyId companyId,
        string profileTag,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new ArgumentException("companyId is required.", nameof(companyId));
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);

        // Single round-trip: set the flag (idempotent — only stamps
        // enabled_at the very first time), refresh profile_tag, return
        // the post-state. updated_at moves on every call so audit
        // trail captures every wizard re-run.
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update companies
            set inventory_module_enabled = true,
                inventory_module_enabled_at = coalesce(inventory_module_enabled_at, now()),
                inventory_profile_tag = nullif(@profile_tag, ''),
                updated_at = now()
            where id = @company_id
            returning id, inventory_module_enabled,
                      inventory_module_enabled_at, inventory_module_locked_at,
                      inventory_profile_tag;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("profile_tag", profileTag ?? string.Empty);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Company {companyId:D} not found while activating Inventory module.");
        }
        return ReadState(reader);
    }

    public async Task<InventoryModuleActivationStateRecord?> GetStateAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            return null;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, inventory_module_enabled,
                   inventory_module_enabled_at, inventory_module_locked_at,
                   inventory_profile_tag
            from companies
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadState(reader);
    }

    private static InventoryModuleActivationStateRecord ReadState(NpgsqlDataReader reader) =>
        new(
            CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            ModuleEnabled: reader.GetBoolean(reader.GetOrdinal("inventory_module_enabled")),
            EnabledAt: reader.IsDBNull(reader.GetOrdinal("inventory_module_enabled_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("inventory_module_enabled_at")),
            LockedAt: reader.IsDBNull(reader.GetOrdinal("inventory_module_locked_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("inventory_module_locked_at")),
            ProfileTag: reader.IsDBNull(reader.GetOrdinal("inventory_profile_tag"))
                ? null
                : reader.GetString(reader.GetOrdinal("inventory_profile_tag")));
}
