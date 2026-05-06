using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryReportingStore : IInventoryReportingStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;

    public PostgreSqlInventoryReportingStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<InventoryAvailabilityDashboard> GetAvailabilityDashboardAsync(
        CompanyId companyId,
        InventoryAvailabilityFilter filter,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);

        var normalizedFilter = NormalizeFilter(filter);
        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, companyId, cancellationToken);
        var availabilityRows = await LoadAvailabilityRowsAsync(connection, companyId, cancellationToken);
        var recentLedgerEntries = await LoadRecentLedgerEntriesAsync(connection, companyId, null, cancellationToken);
        var drillDown = await BuildDrillDownAsync(
            connection,
            companyId,
            normalizedFilter,
            activeItems,
            activeWarehouses,
            availabilityRows,
            cancellationToken);

        return new InventoryAvailabilityDashboard(
            companyId,
            baseCurrencyCode,
            activeItems,
            activeWarehouses,
            availabilityRows,
            recentLedgerEntries,
            drillDown);
    }

    private static InventoryAvailabilityFilter NormalizeFilter(InventoryAvailabilityFilter filter) =>
        new(
            filter.ItemId == Guid.Empty ? null : filter.ItemId,
            filter.WarehouseId == Guid.Empty ? null : filter.WarehouseId);

    private static async Task<string> LoadCompanyBaseCurrencyCodeAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select base_currency_code
            from companies
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string code || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("The active company could not be found.");
        }

        return code;
    }

    private static async Task<IReadOnlyList<InventoryManagedItemSummary>> LoadActiveItemsAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              item_code,
              name,
              description,
              item_kind,
              stock_uom_code,
              manage_inventory_method,
              default_costing_method,
              backorder_mode,
              low_stock_activity,
              default_inventory_asset_account_id,
              default_cogs_account_id,
              default_writeoff_account_id,
              default_purchase_variance_account_id,
              is_active,
              updated_at
            from inventory_items
            where company_id = @company_id
              and is_active = true
              and item_kind = 'stock'
              and manage_inventory_method = 'manage_stock'
            order by item_code asc, name asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var items = new List<InventoryManagedItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new InventoryManagedItemSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                ParseItemKind(reader.GetString(reader.GetOrdinal("item_kind"))),
                reader.IsDBNull(reader.GetOrdinal("stock_uom_code")) ? null : reader.GetString(reader.GetOrdinal("stock_uom_code")),
                ParseManageInventoryMethod(reader.GetString(reader.GetOrdinal("manage_inventory_method"))),
                ParseCostingMethod(reader.GetString(reader.GetOrdinal("default_costing_method"))),
                ParseBackorderMode(reader.GetString(reader.GetOrdinal("backorder_mode"))),
                ParseLowStockActivity(reader.GetString(reader.GetOrdinal("low_stock_activity"))),
                reader.IsDBNull(reader.GetOrdinal("default_inventory_asset_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("default_inventory_asset_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_cogs_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("default_cogs_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_writeoff_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("default_writeoff_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_purchase_variance_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("default_purchase_variance_account_id")),
                null,
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return items;
    }

    private static async Task<IReadOnlyList<InventoryManagedWarehouseSummary>> LoadActiveWarehousesAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              warehouse_code,
              name,
              description,
              is_active,
              updated_at
            from inventory_warehouses
            where company_id = @company_id
              and is_active = true
            order by warehouse_code asc, name asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var warehouses = new List<InventoryManagedWarehouseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            warehouses.Add(new InventoryManagedWarehouseSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return warehouses;
    }

    private static async Task<IReadOnlyList<InventoryItemAvailabilitySummary>> LoadAvailabilityRowsAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with cost_balance as (
              select
                company_id,
                item_id,
                warehouse_id,
                coalesce(sum(remaining_cost_base), 0) as remaining_cost_base
              from inventory_cost_layers
              where company_id = @company_id
              group by company_id, item_id, warehouse_id
            ),
            movement_time as (
              select
                company_id,
                item_id,
                warehouse_id,
                max(created_at) as last_movement_at
              from inventory_ledger_entries
              where company_id = @company_id
              group by company_id, item_id, warehouse_id
            )
            select
              b.item_id,
              i.item_code,
              i.name as item_name,
              b.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              b.on_hand_qty,
              b.reserved_qty,
              (b.on_hand_qty - b.reserved_qty) as available_qty,
              b.in_transit_out_qty,
              b.in_transit_in_qty,
              coalesce(cb.remaining_cost_base, 0) as cost_balance_base,
              mt.last_movement_at
            from item_warehouse_balances b
            join inventory_items i
              on i.id = b.item_id
             and i.company_id = b.company_id
            join inventory_warehouses w
              on w.id = b.warehouse_id
             and w.company_id = b.company_id
            left join cost_balance cb
              on cb.company_id = b.company_id
             and cb.item_id = b.item_id
             and cb.warehouse_id = b.warehouse_id
            left join movement_time mt
              on mt.company_id = b.company_id
             and mt.item_id = b.item_id
             and mt.warehouse_id = b.warehouse_id
            where b.company_id = @company_id
            order by i.item_code asc, w.warehouse_code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<InventoryItemAvailabilitySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InventoryItemAvailabilitySummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("on_hand_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("reserved_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("available_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("in_transit_out_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("in_transit_in_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("cost_balance_base")),
                reader.IsDBNull(reader.GetOrdinal("last_movement_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_movement_at"))));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<InventoryLedgerEntrySummary>> LoadRecentLedgerEntriesAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        InventoryAvailabilityFilter? filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              l.id,
              l.posting_date,
              l.movement_type,
              l.movement_direction,
              l.item_id,
              i.item_code,
              i.name as item_name,
              l.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              l.quantity_delta,
              l.quantity_after,
              l.cost_amount_delta_base,
              l.cost_amount_after_base,
              l.memo,
              l.created_at
            from inventory_ledger_entries l
            join inventory_items i
              on i.id = l.item_id
             and i.company_id = l.company_id
            join inventory_warehouses w
              on w.id = l.warehouse_id
             and w.company_id = l.company_id
            left join inventory_documents d
              on d.id = l.document_id
            where l.company_id = @company_id
              and (@item_id is null or l.item_id = @item_id)
              and (@warehouse_id is null or l.warehouse_id = @warehouse_id)
            order by l.posting_date desc, l.created_at desc
            limit @limit;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", filter?.ItemId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("warehouse_id", filter?.WarehouseId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("limit", filter is null ? 50 : 200);

        var rows = new List<InventoryLedgerEntrySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InventoryLedgerEntrySummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetString(reader.GetOrdinal("movement_type")),
                reader.GetString(reader.GetOrdinal("movement_direction")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("quantity_delta")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("quantity_after")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("cost_amount_delta_base")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("cost_amount_after_base")),
                reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return rows;
    }

    private static Task<InventoryAvailabilityLedgerDrillDown?> BuildDrillDownAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        InventoryAvailabilityFilter filter,
        IReadOnlyList<InventoryManagedItemSummary> activeItems,
        IReadOnlyList<InventoryManagedWarehouseSummary> activeWarehouses,
        IReadOnlyList<InventoryItemAvailabilitySummary> availabilityRows,
        CancellationToken cancellationToken)
    {
        if (filter.ItemId is null && filter.WarehouseId is null)
        {
            return Task.FromResult<InventoryAvailabilityLedgerDrillDown?>(null);
        }

        var matchingBalances = availabilityRows
            .Where(row => (!filter.ItemId.HasValue || row.ItemId == filter.ItemId.Value)
                          && (!filter.WarehouseId.HasValue || row.WarehouseId == filter.WarehouseId.Value))
            .ToArray();

        var itemDisplayText = filter.ItemId.HasValue
            ? activeItems.FirstOrDefault(item => item.Id == filter.ItemId.Value) is { } item
                ? $"{item.ItemCode} - {item.Name}"
                : null
            : null;
        var warehouseDisplayText = filter.WarehouseId.HasValue
            ? activeWarehouses.FirstOrDefault(warehouse => warehouse.Id == filter.WarehouseId.Value) is { } warehouse
                ? $"{warehouse.WarehouseCode} - {warehouse.Name}"
                : null
            : null;

        return BuildDrillDownAsyncCore(
            connection,
            companyId,
            filter,
            itemDisplayText,
            warehouseDisplayText,
            matchingBalances,
            cancellationToken);
    }

    private static async Task<InventoryAvailabilityLedgerDrillDown?> BuildDrillDownAsyncCore(
        NpgsqlConnection connection,
        CompanyId companyId,
        InventoryAvailabilityFilter filter,
        string? itemDisplayText,
        string? warehouseDisplayText,
        IReadOnlyList<InventoryItemAvailabilitySummary> matchingBalances,
        CancellationToken cancellationToken)
    {
        var ledgerEntries = await LoadRecentLedgerEntriesAsync(connection, companyId, filter, cancellationToken);
        return new InventoryAvailabilityLedgerDrillDown(
            filter,
            itemDisplayText,
            warehouseDisplayText,
            matchingBalances,
            ledgerEntries);
    }

    private static InventoryCostingMethod ParseCostingMethod(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "moving_average" => InventoryCostingMethod.MovingAverage,
            "fifo" => InventoryCostingMethod.Fifo,
            _ => throw new InvalidOperationException($"Unsupported inventory costing method '{value}'.")
        };

    private static InventoryItemKind ParseItemKind(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "stock" => InventoryItemKind.Stock,
            "non_stock" => InventoryItemKind.NonStock,
            "service" => InventoryItemKind.Service,
            "drop_ship" => InventoryItemKind.DropShip,
            _ => throw new InvalidOperationException($"Unsupported inventory item kind '{value}'.")
        };

    private static ManageInventoryMethod ParseManageInventoryMethod(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "dont_manage_stock" => ManageInventoryMethod.DontManageStock,
            "manage_stock" => ManageInventoryMethod.ManageStock,
            "manage_stock_by_sku" => ManageInventoryMethod.ManageStockBySku,
            _ => throw new InvalidOperationException($"Unsupported inventory management method '{value}'.")
        };

    private static InventoryBackorderMode ParseBackorderMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "disallow" => InventoryBackorderMode.Disallow,
            "allow_negative" => InventoryBackorderMode.AllowNegative,
            "allow_negative_with_warning" => InventoryBackorderMode.AllowNegativeWithWarning,
            _ => throw new InvalidOperationException($"Unsupported inventory backorder mode '{value}'.")
        };

    private static InventoryLowStockActivity ParseLowStockActivity(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "nothing" => InventoryLowStockActivity.Nothing,
            "warn" => InventoryLowStockActivity.Warn,
            "block_outbound" => InventoryLowStockActivity.BlockOutbound,
            _ => throw new InvalidOperationException($"Unsupported inventory low-stock activity '{value}'.")
        };
}
