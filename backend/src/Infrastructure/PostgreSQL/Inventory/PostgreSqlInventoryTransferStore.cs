using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryTransferStore : IInventoryTransferStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryTransferStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<InventoryTransferDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, null, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, null, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, null, companyId, cancellationToken);
        var recentTransfers = await LoadRecentTransfersAsync(connection, null, companyId, cancellationToken);

        return new InventoryTransferDashboard(
            companyId,
            baseCurrencyCode,
            activeItems,
            activeWarehouses,
            recentTransfers);
    }

    public async Task<InventoryTransferSummary> UpsertAsync(
        InventoryTransferUpsertRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var warehouseMap = await LoadWarehouseMapAsync(
                connection,
                transaction,
                request.CompanyId,
                [request.SourceWarehouseId, request.DestinationWarehouseId],
                cancellationToken);
            if (!warehouseMap.ContainsKey(request.SourceWarehouseId) ||
                !warehouseMap.ContainsKey(request.DestinationWarehouseId))
            {
                throw new InvalidOperationException("Transfer must use active warehouses in this company.");
            }

            var itemMap = await LoadItemMapAsync(
                connection,
                transaction,
                request.CompanyId,
                request.Lines.Select(line => line.ItemId).Distinct().ToArray(),
                cancellationToken);

            foreach (var line in request.Lines)
            {
                if (!itemMap.TryGetValue(line.ItemId, out var item))
                {
                    throw new InvalidOperationException("Each transfer line must reference an active inventory item in this company.");
                }

                if (item.ItemKind != InventoryItemKind.Stock)
                {
                    throw new InvalidOperationException($"Warehouse transfer only supports stock items. '{item.Name}' is not a stock item.");
                }

                if (item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
                {
                    throw new InvalidOperationException($"Warehouse transfer currently supports only warehouse-managed stock items. '{item.Name}' is not configured on that path.");
                }

                if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Transfer line UOM must match the stock UOM for '{item.Name}'.");
                }
            }

            var transferId = request.TransferId ?? Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            string transferNumber;

            if (request.TransferId.HasValue)
            {
                var existing = await LoadTransferForUpdateAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    request.TransferId.Value,
                    cancellationToken);
                if (existing is null)
                {
                    throw new InvalidOperationException("Transfer draft was not found in this company.");
                }

                if (!string.Equals(existing.Status, "draft", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Only draft transfers can be edited.");
                }

                transferNumber = existing.TransferNumber;

                await using (var updateTransferCommand = connection.CreateCommand())
                {
                    updateTransferCommand.Transaction = transaction;
                    updateTransferCommand.CommandText =
                        """
                        update warehouse_transfers
                        set source_warehouse_id = @source_warehouse_id,
                            destination_warehouse_id = @destination_warehouse_id,
                            memo = @memo
                        where id = @id
                          and company_id = @company_id;
                        """;
                    updateTransferCommand.Parameters.AddWithValue("id", transferId);
                    updateTransferCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                    updateTransferCommand.Parameters.AddWithValue("source_warehouse_id", request.SourceWarehouseId);
                    updateTransferCommand.Parameters.AddWithValue("destination_warehouse_id", request.DestinationWarehouseId);
                    updateTransferCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                    await updateTransferCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var deleteLinesCommand = connection.CreateCommand())
                {
                    deleteLinesCommand.Transaction = transaction;
                    deleteLinesCommand.CommandText =
                        """
                        delete from warehouse_transfer_lines
                        where transfer_id = @transfer_id;
                        """;
                    deleteLinesCommand.Parameters.AddWithValue("transfer_id", transferId);
                    await deleteLinesCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            else
            {
                transferNumber = BuildTransferNumber();

                await using var insertTransferCommand = connection.CreateCommand();
                insertTransferCommand.Transaction = transaction;
                insertTransferCommand.CommandText =
                    """
                    insert into warehouse_transfers (
                      id,
                      company_id,
                      transfer_number,
                      status,
                      source_warehouse_id,
                      destination_warehouse_id,
                      requested_by_user_id,
                      memo,
                      created_at
                    )
                    values (
                      @id,
                      @company_id,
                      @transfer_number,
                      'draft',
                      @source_warehouse_id,
                      @destination_warehouse_id,
                      @requested_by_user_id,
                      @memo,
                      @created_at
                    );
                    """;
                insertTransferCommand.Parameters.AddWithValue("id", transferId);
                insertTransferCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                insertTransferCommand.Parameters.AddWithValue("transfer_number", transferNumber);
                insertTransferCommand.Parameters.AddWithValue("source_warehouse_id", request.SourceWarehouseId);
                insertTransferCommand.Parameters.AddWithValue("destination_warehouse_id", request.DestinationWarehouseId);
                insertTransferCommand.Parameters.AddWithValue("requested_by_user_id", request.UserId);
                insertTransferCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertTransferCommand.Parameters.AddWithValue("created_at", createdAt);
                await insertTransferCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                await using var insertLineCommand = connection.CreateCommand();
                insertLineCommand.Transaction = transaction;
                insertLineCommand.CommandText =
                    """
                    insert into warehouse_transfer_lines (
                      id,
                      company_id,
                      transfer_id,
                      line_no,
                      item_id,
                      uom_code,
                      quantity,
                      base_quantity,
                      memo
                    )
                    values (
                      gen_random_uuid(),
                      @company_id,
                      @transfer_id,
                      @line_no,
                      @item_id,
                      @uom_code,
                      @quantity,
                      @base_quantity,
                      @memo
                    );
                    """;
                insertLineCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                insertLineCommand.Parameters.AddWithValue("transfer_id", transferId);
                insertLineCommand.Parameters.AddWithValue("line_no", line.LineNo);
                insertLineCommand.Parameters.AddWithValue("item_id", line.ItemId);
                insertLineCommand.Parameters.AddWithValue("uom_code", line.UomCode.Trim().ToUpperInvariant());
                insertLineCommand.Parameters.AddWithValue("quantity", line.Quantity);
                insertLineCommand.Parameters.AddWithValue("base_quantity", decimal.Round(line.Quantity, 6, MidpointRounding.AwayFromZero));
                insertLineCommand.Parameters.AddWithValue("memo", ToDbValue(line.Memo));
                await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return await LoadTransferSummaryAsync(connection, null, request.CompanyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Transfer draft could not be reloaded after save.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<InventoryTransferSummary> SubmitAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var transfer = await LoadTransferForUpdateAsync(connection, transaction, companyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Transfer was not found in this company.");
            if (!string.Equals(transfer.Status, "draft", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only draft transfers can be submitted.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                update warehouse_transfers
                set status = 'submitted',
                    submitted_at = @submitted_at,
                    submitted_by_user_id = @submitted_by_user_id
                where id = @id
                  and company_id = @company_id;
                """;
            command.Parameters.AddWithValue("id", transferId);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("submitted_at", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("submitted_by_user_id", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return await LoadTransferSummaryAsync(connection, null, companyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Submitted transfer could not be reloaded.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task<InventoryTransferSummary> ShipAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken) =>
        ShipCoreAsync(companyId, transferId, userId, postingDate, cancellationToken);

    public Task<InventoryTransferSummary> ReceiveAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken) =>
        ReceiveCoreAsync(companyId, transferId, userId, postingDate, cancellationToken);

    private async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                alter table warehouse_transfer_lines
                  add column if not exists uom_code text;

                update warehouse_transfer_lines
                set uom_code = coalesce(nullif(trim(uom_code), ''), 'EA')
                where uom_code is null
                   or btrim(uom_code) = '';

                alter table warehouse_transfers
                  add column if not exists submitted_at timestamptz null;

                alter table warehouse_transfers
                  add column if not exists submitted_by_user_id char(7) null;

                alter table warehouse_transfers
                  add column if not exists shipped_by_user_id char(7) null;

                alter table warehouse_transfers
                  add column if not exists received_by_user_id char(7) null;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task<string> LoadCompanyBaseCurrencyCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select base_currency_code
            from companies
            where id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is string baseCurrencyCode && !string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            return baseCurrencyCode.Trim().ToUpperInvariant();
        }

        throw new InvalidOperationException("Company base currency could not be resolved for inventory transfer.");
    }

    private static async Task<Dictionary<Guid, InventoryManagedItemSummary>> LoadItemMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
              and id = any(@item_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_ids", itemIds.ToArray());

        var items = new Dictionary<Guid, InventoryManagedItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new InventoryManagedItemSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                ParseItemKind(reader.GetString(reader.GetOrdinal("item_kind"))),
                reader.IsDBNull(reader.GetOrdinal("stock_uom_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("stock_uom_code")),
                ParseManageInventoryMethod(reader.GetString(reader.GetOrdinal("manage_inventory_method"))),
                ParseCostingMethod(reader.GetString(reader.GetOrdinal("default_costing_method"))),
                ParseBackorderMode(reader.GetString(reader.GetOrdinal("backorder_mode"))),
                ParseLowStockActivity(reader.GetString(reader.GetOrdinal("low_stock_activity"))),
                reader.IsDBNull(reader.GetOrdinal("default_inventory_asset_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_inventory_asset_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_cogs_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_cogs_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_writeoff_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_writeoff_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_purchase_variance_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_purchase_variance_account_id")),
                null,
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
            items[item.Id] = item;
        }

        return items;
    }

    private static async Task<IReadOnlyList<InventoryManagedItemSummary>> LoadActiveItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                ParseItemKind(reader.GetString(reader.GetOrdinal("item_kind"))),
                reader.IsDBNull(reader.GetOrdinal("stock_uom_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("stock_uom_code")),
                ParseManageInventoryMethod(reader.GetString(reader.GetOrdinal("manage_inventory_method"))),
                ParseCostingMethod(reader.GetString(reader.GetOrdinal("default_costing_method"))),
                ParseBackorderMode(reader.GetString(reader.GetOrdinal("backorder_mode"))),
                ParseLowStockActivity(reader.GetString(reader.GetOrdinal("low_stock_activity"))),
                reader.IsDBNull(reader.GetOrdinal("default_inventory_asset_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_inventory_asset_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_cogs_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_cogs_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_writeoff_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_writeoff_account_id")),
                null,
                reader.IsDBNull(reader.GetOrdinal("default_purchase_variance_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("default_purchase_variance_account_id")),
                null,
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return items;
    }

    private static async Task<Dictionary<Guid, InventoryManagedWarehouseSummary>> LoadWarehouseMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> warehouseIds,
        CancellationToken cancellationToken)
    {
        if (warehouseIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
              and id = any(@warehouse_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("warehouse_ids", warehouseIds.ToArray());

        var warehouses = new Dictionary<Guid, InventoryManagedWarehouseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var warehouse = new InventoryManagedWarehouseSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
            warehouses[warehouse.Id] = warehouse;
        }

        return warehouses;
    }

    private static async Task<IReadOnlyList<InventoryManagedWarehouseSummary>> LoadActiveWarehousesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return warehouses;
    }

    private static async Task<InventoryTransferHeader?> LoadTransferForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              company_id,
              transfer_number,
              status,
              source_warehouse_id,
              destination_warehouse_id,
              requested_by_user_id,
              memo,
              created_at,
              submitted_at,
              shipped_at,
              received_at
            from warehouse_transfers
            where company_id = @company_id
              and id = @transfer_id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("transfer_id", transferId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryTransferHeader(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("transfer_number")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetGuid(reader.GetOrdinal("source_warehouse_id")),
            reader.GetGuid(reader.GetOrdinal("destination_warehouse_id")),
            UserId.Parse(reader.GetString(reader.GetOrdinal("requested_by_user_id"))),
            reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("submitted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("submitted_at")),
            reader.IsDBNull(reader.GetOrdinal("shipped_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("shipped_at")),
            reader.IsDBNull(reader.GetOrdinal("received_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("received_at")));
    }

    private static async Task<IReadOnlyList<InventoryTransferLineRow>> LoadTransferLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              line_no,
              item_id,
              uom_code,
              quantity,
              base_quantity,
              memo
            from warehouse_transfer_lines
            where company_id = @company_id
              and transfer_id = @transfer_id
            order by line_no asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("transfer_id", transferId);

        var lines = new List<InventoryTransferLineRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new InventoryTransferLineRow(
                reader.GetInt32(reader.GetOrdinal("line_no")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("quantity")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("base_quantity")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return lines;
    }

    private static async Task<Dictionary<int, decimal>> LoadShippedLineCostsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              l.line_no,
              l.extended_cost_base
            from inventory_documents d
            inner join inventory_document_lines l
              on l.document_id = d.id
            where d.company_id = @company_id
              and d.document_type = 'transfer_ship'
              and d.source_document_id = @source_document_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_document_id", transferId);

        var costs = new Dictionary<int, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            costs[reader.GetInt32(reader.GetOrdinal("line_no"))] = reader.GetFieldValue<decimal>(reader.GetOrdinal("extended_cost_base"));
        }

        return costs;
    }

    private static async Task<InventoryTransferSummary?> LoadTransferSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid transferId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              t.id as transfer_id,
              t.company_id,
              t.transfer_number,
              t.status,
              t.source_warehouse_id,
              sw.warehouse_code as source_warehouse_code,
              sw.name as source_warehouse_name,
              t.destination_warehouse_id,
              dw.warehouse_code as destination_warehouse_code,
              dw.name as destination_warehouse_name,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              count(l.id) as line_count,
              t.created_at,
              t.submitted_at,
              t.shipped_at,
              t.received_at,
              t.memo
            from warehouse_transfers t
            inner join inventory_warehouses sw
              on sw.id = t.source_warehouse_id
            inner join inventory_warehouses dw
              on dw.id = t.destination_warehouse_id
            left join warehouse_transfer_lines l
              on l.transfer_id = t.id
            where t.company_id = @company_id
              and t.id = @transfer_id
            group by
              t.id,
              t.company_id,
              t.transfer_number,
              t.status,
              t.source_warehouse_id,
              sw.warehouse_code,
              sw.name,
              t.destination_warehouse_id,
              dw.warehouse_code,
              dw.name,
              t.created_at,
              t.submitted_at,
              t.shipped_at,
              t.received_at,
              t.memo;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("transfer_id", transferId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryTransferSummary(
            reader.GetGuid(reader.GetOrdinal("transfer_id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("transfer_number")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetGuid(reader.GetOrdinal("source_warehouse_id")),
            reader.GetString(reader.GetOrdinal("source_warehouse_code")),
            reader.GetString(reader.GetOrdinal("source_warehouse_name")),
            reader.GetGuid(reader.GetOrdinal("destination_warehouse_id")),
            reader.GetString(reader.GetOrdinal("destination_warehouse_code")),
            reader.GetString(reader.GetOrdinal("destination_warehouse_name")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")),
            reader.GetInt32(reader.GetOrdinal("line_count")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("submitted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("submitted_at")),
            reader.IsDBNull(reader.GetOrdinal("shipped_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("shipped_at")),
            reader.IsDBNull(reader.GetOrdinal("received_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("received_at")),
            reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo")));
    }

    private static async Task<IReadOnlyList<InventoryTransferSummary>> LoadRecentTransfersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              t.id as transfer_id,
              t.company_id,
              t.transfer_number,
              t.status,
              t.source_warehouse_id,
              sw.warehouse_code as source_warehouse_code,
              sw.name as source_warehouse_name,
              t.destination_warehouse_id,
              dw.warehouse_code as destination_warehouse_code,
              dw.name as destination_warehouse_name,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              count(l.id) as line_count,
              t.created_at,
              t.submitted_at,
              t.shipped_at,
              t.received_at,
              t.memo
            from warehouse_transfers t
            inner join inventory_warehouses sw
              on sw.id = t.source_warehouse_id
            inner join inventory_warehouses dw
              on dw.id = t.destination_warehouse_id
            left join warehouse_transfer_lines l
              on l.transfer_id = t.id
            where t.company_id = @company_id
            group by
              t.id,
              t.company_id,
              t.transfer_number,
              t.status,
              t.source_warehouse_id,
              sw.warehouse_code,
              sw.name,
              t.destination_warehouse_id,
              dw.warehouse_code,
              dw.name,
              t.created_at,
              t.submitted_at,
              t.shipped_at,
              t.received_at,
              t.memo
            order by t.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var transfers = new List<InventoryTransferSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transfers.Add(new InventoryTransferSummary(
                reader.GetGuid(reader.GetOrdinal("transfer_id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("transfer_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetGuid(reader.GetOrdinal("source_warehouse_id")),
                reader.GetString(reader.GetOrdinal("source_warehouse_code")),
                reader.GetString(reader.GetOrdinal("source_warehouse_name")),
                reader.GetGuid(reader.GetOrdinal("destination_warehouse_id")),
                reader.GetString(reader.GetOrdinal("destination_warehouse_code")),
                reader.GetString(reader.GetOrdinal("destination_warehouse_name")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("submitted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("submitted_at")),
                reader.IsDBNull(reader.GetOrdinal("shipped_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("shipped_at")),
                reader.IsDBNull(reader.GetOrdinal("received_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("received_at")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return transfers;
    }

    private static async Task<ItemWarehouseBalanceSnapshot> LoadCurrentBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              coalesce(on_hand_qty, 0) as on_hand_qty,
              coalesce(reserved_qty, 0) as reserved_qty,
              coalesce(in_transit_out_qty, 0) as in_transit_out_qty,
              coalesce(in_transit_in_qty, 0) as in_transit_in_qty
            from item_warehouse_balances
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new ItemWarehouseBalanceSnapshot(
                reader.GetFieldValue<decimal>(reader.GetOrdinal("on_hand_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("reserved_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("in_transit_out_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("in_transit_in_qty")));
        }

        return new ItemWarehouseBalanceSnapshot(0m, 0m, 0m, 0m);
    }

    private static async Task<decimal> LoadCurrentCostBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(sum(remaining_cost_base), 0)
            from inventory_cost_layers
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is decimal decimalResult ? decimalResult : 0m;
    }

    private static async Task<IReadOnlyList<OpenCostLayer>> LoadOpenCostLayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              remaining_qty,
              remaining_cost_base,
              unit_cost_base,
              layer_date,
              created_at
            from inventory_cost_layers
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id
              and remaining_qty > 0
            order by layer_date asc, created_at asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        var layers = new List<OpenCostLayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            layers.Add(new OpenCostLayer(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_cost_base")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("unit_cost_base")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("layer_date")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return layers;
    }

    private async Task<InventoryTransferSummary> ShipCoreAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        var foundationSummary = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var transfer = await LoadTransferForUpdateAsync(connection, transaction, companyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Transfer was not found in this company.");
            if (!string.Equals(transfer.Status, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only submitted transfers can be shipped.");
            }

            var transferLines = await LoadTransferLinesAsync(connection, transaction, companyId, transferId, cancellationToken);
            if (transferLines.Count == 0)
            {
                throw new InvalidOperationException("Transfer must contain at least one line before it can be shipped.");
            }

            var itemMap = await LoadItemMapAsync(
                connection,
                transaction,
                companyId,
                transferLines.Select(line => line.ItemId).Distinct().ToArray(),
                cancellationToken);
            var warehouseMap = await LoadWarehouseMapAsync(
                connection,
                transaction,
                companyId,
                [transfer.SourceWarehouseId, transfer.DestinationWarehouseId],
                cancellationToken);
            if (!warehouseMap.ContainsKey(transfer.SourceWarehouseId) ||
                !warehouseMap.ContainsKey(transfer.DestinationWarehouseId))
            {
                throw new InvalidOperationException("Transfer warehouses must remain active through ship.");
            }

            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, companyId, cancellationToken);
            var shipDocumentId = Guid.NewGuid();
            var movementTimestamp = DateTimeOffset.UtcNow;
            var negativeStockAllowed = foundationSummary.CostingPolicy?.NegativeStockAllowed == true;

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
                    """
                    insert into inventory_documents (
                      id, company_id, document_type, status, movement_direction, posting_date, source_module, source_document_id, source_document_number, memo, created_by_user_id, created_at, posted_at
                    )
                    values (
                      @id, @company_id, 'transfer_ship', 'shipped', 'internal', @posting_date, 'warehouse_transfer', @source_document_id, @source_document_number, @memo, @created_by_user_id, @created_at, @posted_at
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", shipDocumentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", companyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", postingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_document_id", transferId);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", transfer.TransferNumber);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(transfer.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", userId);
                insertDocumentCommand.Parameters.AddWithValue("created_at", movementTimestamp);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", movementTimestamp);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in transferLines.OrderBy(line => line.LineNo))
            {
                if (!itemMap.TryGetValue(line.ItemId, out var item))
                {
                    throw new InvalidOperationException("Each transfer line must reference an active inventory item in this company.");
                }

                if (item.ItemKind != InventoryItemKind.Stock ||
                    item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
                {
                    throw new InvalidOperationException($"Transfer ship currently supports only warehouse-managed stock items. '{item.Name}' is not configured on that path.");
                }

                if (!string.Equals(line.UomCode, item.StockUomCode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Transfer line UOM must match the stock UOM for '{item.Name}'.");
                }

                var balance = await LoadCurrentBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.SourceWarehouseId, cancellationToken);
                var availableQuantity = decimal.Round(balance.OnHandQty - balance.ReservedQty, 6, MidpointRounding.AwayFromZero);
                if (!negativeStockAllowed && availableQuantity < line.BaseQuantity)
                {
                    throw new InvalidOperationException($"Transfer ship cannot post because '{item.Name}' only has {availableQuantity} available in the source warehouse.");
                }

                var costLayers = await LoadOpenCostLayersAsync(connection, transaction, companyId, line.ItemId, transfer.SourceWarehouseId, cancellationToken);
                var lineCostResult = item.DefaultCostingMethod switch
                {
                    InventoryCostingMethod.Fifo => ConsumeFifo(costLayers, line.BaseQuantity, item.Name),
                    InventoryCostingMethod.MovingAverage => ConsumeMovingAverage(costLayers, line.BaseQuantity, item.Name),
                    _ => throw new InvalidOperationException($"Unsupported inventory costing method '{item.DefaultCostingMethod}'.")
                };

                var shipLineDocumentId = Guid.NewGuid();
                var shipLedgerEntryId = Guid.NewGuid();
                var unitCostBase = line.BaseQuantity == 0 ? 0m : decimal.Round(lineCostResult.TotalCostBase / line.BaseQuantity, 6, MidpointRounding.AwayFromZero);
                var sourceCostBalance = await LoadCurrentCostBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.SourceWarehouseId, cancellationToken);
                var sourceQuantityAfter = decimal.Round(balance.OnHandQty - line.BaseQuantity, 6, MidpointRounding.AwayFromZero);
                var sourceCostAfter = decimal.Round(sourceCostBalance - lineCostResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);

                await InsertInventoryDocumentLineAsync(
                    connection,
                    transaction,
                    companyId,
                    shipLineDocumentId,
                    shipDocumentId,
                    line.LineNo,
                    line.ItemId,
                    transfer.SourceWarehouseId,
                    line.UomCode,
                    line.Quantity,
                    line.BaseQuantity,
                    baseCurrencyCode,
                    unitCostBase,
                    lineCostResult.TotalCostBase,
                    line.Memo,
                    cancellationToken);

                await AdjustBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.SourceWarehouseId, -line.BaseQuantity, 0m, line.BaseQuantity, 0m, cancellationToken);
                await AdjustBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.DestinationWarehouseId, 0m, 0m, 0m, line.BaseQuantity, cancellationToken);

                await InsertLedgerEntryAsync(
                    connection,
                    transaction,
                    shipLedgerEntryId,
                    companyId,
                    line.ItemId,
                    transfer.SourceWarehouseId,
                    shipDocumentId,
                    shipLineDocumentId,
                    "transfer_ship",
                    postingDate,
                    -line.BaseQuantity,
                    sourceQuantityAfter,
                    -lineCostResult.TotalCostBase,
                    sourceCostAfter,
                    BuildShipLedgerMemo(transfer.TransferNumber, line.LineNo),
                    movementTimestamp,
                    cancellationToken);

                foreach (var consumption in lineCostResult.Consumptions)
                {
                    await using (var updateLayerCommand = connection.CreateCommand())
                    {
                        updateLayerCommand.Transaction = transaction;
                        updateLayerCommand.CommandText =
                            """
                            update inventory_cost_layers
                            set remaining_qty = @remaining_qty,
                                remaining_cost_base = @remaining_cost_base
                            where id = @id;
                            """;
                        updateLayerCommand.Parameters.AddWithValue("id", consumption.CostLayerId);
                        updateLayerCommand.Parameters.AddWithValue("remaining_qty", consumption.RemainingQtyAfter);
                        updateLayerCommand.Parameters.AddWithValue("remaining_cost_base", consumption.RemainingCostAfter);
                        await updateLayerCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using var insertConsumptionCommand = connection.CreateCommand();
                    insertConsumptionCommand.Transaction = transaction;
                    insertConsumptionCommand.CommandText =
                        """
                        insert into inventory_layer_consumptions (
                          id, company_id, issue_ledger_entry_id, cost_layer_id, consumed_qty, consumed_cost_base, created_at
                        )
                        values (
                          gen_random_uuid(), @company_id, @issue_ledger_entry_id, @cost_layer_id, @consumed_qty, @consumed_cost_base, @created_at
                        );
                        """;
                    insertConsumptionCommand.Parameters.AddWithValue("company_id", companyId.Value);
                    insertConsumptionCommand.Parameters.AddWithValue("issue_ledger_entry_id", shipLedgerEntryId);
                    insertConsumptionCommand.Parameters.AddWithValue("cost_layer_id", consumption.CostLayerId);
                    insertConsumptionCommand.Parameters.AddWithValue("consumed_qty", consumption.ConsumedQty);
                    insertConsumptionCommand.Parameters.AddWithValue("consumed_cost_base", consumption.ConsumedCostBase);
                    insertConsumptionCommand.Parameters.AddWithValue("created_at", movementTimestamp);
                    await insertConsumptionCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var updateTransferCommand = connection.CreateCommand())
            {
                updateTransferCommand.Transaction = transaction;
                updateTransferCommand.CommandText =
                    """
                    update warehouse_transfers
                    set status = 'shipped',
                        shipped_at = @shipped_at,
                        shipped_by_user_id = @shipped_by_user_id
                    where id = @id
                      and company_id = @company_id;
                    """;
                updateTransferCommand.Parameters.AddWithValue("id", transferId);
                updateTransferCommand.Parameters.AddWithValue("company_id", companyId.Value);
                updateTransferCommand.Parameters.AddWithValue("shipped_at", movementTimestamp);
                updateTransferCommand.Parameters.AddWithValue("shipped_by_user_id", userId);
                await updateTransferCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return await LoadTransferSummaryAsync(connection, null, companyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Shipped transfer could not be reloaded.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<InventoryTransferSummary> ReceiveCoreAsync(
        CompanyId companyId,
        Guid transferId,
        UserId userId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var transfer = await LoadTransferForUpdateAsync(connection, transaction, companyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Transfer was not found in this company.");
            if (!string.Equals(transfer.Status, "shipped", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only shipped transfers can be received.");
            }

            var transferLines = await LoadTransferLinesAsync(connection, transaction, companyId, transferId, cancellationToken);
            var shippedLineCosts = await LoadShippedLineCostsAsync(connection, transaction, companyId, transferId, cancellationToken);
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, companyId, cancellationToken);
            var receiveDocumentId = Guid.NewGuid();
            var movementTimestamp = DateTimeOffset.UtcNow;

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
                    """
                    insert into inventory_documents (
                      id, company_id, document_type, status, movement_direction, posting_date, source_module, source_document_id, source_document_number, memo, created_by_user_id, created_at, posted_at
                    )
                    values (
                      @id, @company_id, 'transfer_receive', 'received', 'internal', @posting_date, 'warehouse_transfer', @source_document_id, @source_document_number, @memo, @created_by_user_id, @created_at, @posted_at
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", receiveDocumentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", companyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", postingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_document_id", transferId);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", transfer.TransferNumber);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(transfer.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", userId);
                insertDocumentCommand.Parameters.AddWithValue("created_at", movementTimestamp);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", movementTimestamp);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in transferLines.OrderBy(line => line.LineNo))
            {
                if (!shippedLineCosts.TryGetValue(line.LineNo, out var shippedCost))
                {
                    throw new InvalidOperationException("Transfer receive could not locate the shipped cost trace for one or more lines.");
                }

                var destinationBalance = await LoadCurrentBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.DestinationWarehouseId, cancellationToken);
                var destinationCostBalance = await LoadCurrentCostBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.DestinationWarehouseId, cancellationToken);
                var receiveDocumentLineId = Guid.NewGuid();
                var receiveLedgerEntryId = Guid.NewGuid();
                var quantityAfter = decimal.Round(destinationBalance.OnHandQty + line.BaseQuantity, 6, MidpointRounding.AwayFromZero);
                var costAfter = decimal.Round(destinationCostBalance + shippedCost, 6, MidpointRounding.AwayFromZero);
                var unitCostBase = line.BaseQuantity == 0 ? 0m : decimal.Round(shippedCost / line.BaseQuantity, 6, MidpointRounding.AwayFromZero);

                await InsertInventoryDocumentLineAsync(
                    connection,
                    transaction,
                    companyId,
                    receiveDocumentLineId,
                    receiveDocumentId,
                    line.LineNo,
                    line.ItemId,
                    transfer.DestinationWarehouseId,
                    line.UomCode,
                    line.Quantity,
                    line.BaseQuantity,
                    baseCurrencyCode,
                    unitCostBase,
                    shippedCost,
                    line.Memo,
                    cancellationToken);

                await AdjustBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.SourceWarehouseId, 0m, 0m, -line.BaseQuantity, 0m, cancellationToken);
                await AdjustBalanceAsync(connection, transaction, companyId, line.ItemId, transfer.DestinationWarehouseId, line.BaseQuantity, 0m, 0m, -line.BaseQuantity, cancellationToken);

                await InsertLedgerEntryAsync(
                    connection,
                    transaction,
                    receiveLedgerEntryId,
                    companyId,
                    line.ItemId,
                    transfer.DestinationWarehouseId,
                    receiveDocumentId,
                    receiveDocumentLineId,
                    "transfer_receive",
                    postingDate,
                    line.BaseQuantity,
                    quantityAfter,
                    shippedCost,
                    costAfter,
                    BuildReceiveLedgerMemo(transfer.TransferNumber, line.LineNo),
                    movementTimestamp,
                    cancellationToken);

                await using var insertLayerCommand = connection.CreateCommand();
                insertLayerCommand.Transaction = transaction;
                insertLayerCommand.CommandText =
                    """
                    insert into inventory_cost_layers (
                      id, company_id, item_id, warehouse_id, source_ledger_entry_id, source_document_id, layer_date, original_qty, remaining_qty, unit_cost_base, remaining_cost_base, created_at
                    )
                    values (
                      gen_random_uuid(), @company_id, @item_id, @warehouse_id, @source_ledger_entry_id, @source_document_id, @layer_date, @original_qty, @remaining_qty, @unit_cost_base, @remaining_cost_base, @created_at
                    );
                    """;
                insertLayerCommand.Parameters.AddWithValue("company_id", companyId.Value);
                insertLayerCommand.Parameters.AddWithValue("item_id", line.ItemId);
                insertLayerCommand.Parameters.AddWithValue("warehouse_id", transfer.DestinationWarehouseId);
                insertLayerCommand.Parameters.AddWithValue("source_ledger_entry_id", receiveLedgerEntryId);
                insertLayerCommand.Parameters.AddWithValue("source_document_id", receiveDocumentId);
                insertLayerCommand.Parameters.AddWithValue("layer_date", postingDate);
                insertLayerCommand.Parameters.AddWithValue("original_qty", line.BaseQuantity);
                insertLayerCommand.Parameters.AddWithValue("remaining_qty", line.BaseQuantity);
                insertLayerCommand.Parameters.AddWithValue("unit_cost_base", unitCostBase);
                insertLayerCommand.Parameters.AddWithValue("remaining_cost_base", shippedCost);
                insertLayerCommand.Parameters.AddWithValue("created_at", movementTimestamp);
                await insertLayerCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var updateTransferCommand = connection.CreateCommand())
            {
                updateTransferCommand.Transaction = transaction;
                updateTransferCommand.CommandText =
                    """
                    update warehouse_transfers
                    set status = 'received',
                        received_at = @received_at,
                        received_by_user_id = @received_by_user_id
                    where id = @id
                      and company_id = @company_id;
                    """;
                updateTransferCommand.Parameters.AddWithValue("id", transferId);
                updateTransferCommand.Parameters.AddWithValue("company_id", companyId.Value);
                updateTransferCommand.Parameters.AddWithValue("received_at", movementTimestamp);
                updateTransferCommand.Parameters.AddWithValue("received_by_user_id", userId);
                await updateTransferCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return await LoadTransferSummaryAsync(connection, null, companyId, transferId, cancellationToken)
                ?? throw new InvalidOperationException("Received transfer could not be reloaded.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task InsertInventoryDocumentLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid lineDocumentId,
        Guid documentId,
        int lineNo,
        Guid itemId,
        Guid warehouseId,
        string uomCode,
        decimal quantity,
        decimal baseQuantity,
        string currencyCode,
        decimal unitCostBase,
        decimal extendedCostBase,
        string? memo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_document_lines (
              id, company_id, document_id, line_no, item_id, warehouse_id, uom_code, quantity, base_quantity, currency_code, fx_rate_to_base, unit_cost_tx, unit_cost_base, extended_cost_base, reason_code, memo
            )
            values (
              @id, @company_id, @document_id, @line_no, @item_id, @warehouse_id, @uom_code, @quantity, @base_quantity, @currency_code, 1, @unit_cost_tx, @unit_cost_base, @extended_cost_base, 'warehouse_transfer', @memo
            );
            """;
        command.Parameters.AddWithValue("id", lineDocumentId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("line_no", lineNo);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("uom_code", uomCode);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("base_quantity", baseQuantity);
        command.Parameters.AddWithValue("currency_code", currencyCode);
        command.Parameters.AddWithValue("unit_cost_tx", unitCostBase);
        command.Parameters.AddWithValue("unit_cost_base", unitCostBase);
        command.Parameters.AddWithValue("extended_cost_base", extendedCostBase);
        command.Parameters.AddWithValue("memo", ToDbValue(memo));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLedgerEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ledgerEntryId,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        Guid documentId,
        Guid documentLineId,
        string movementType,
        DateOnly postingDate,
        decimal quantityDelta,
        decimal quantityAfter,
        decimal costDeltaBase,
        decimal costAfterBase,
        string memo,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_ledger_entries (
              id, company_id, item_id, warehouse_id, document_id, document_line_id, movement_direction, movement_type, posting_date, quantity_delta, quantity_after, cost_amount_delta_base, cost_amount_after_base, memo, created_at
            )
            values (
              @id, @company_id, @item_id, @warehouse_id, @document_id, @document_line_id, 'internal', @movement_type, @posting_date, @quantity_delta, @quantity_after, @cost_amount_delta_base, @cost_amount_after_base, @memo, @created_at
            );
            """;
        command.Parameters.AddWithValue("id", ledgerEntryId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("document_line_id", documentLineId);
        command.Parameters.AddWithValue("movement_type", movementType);
        command.Parameters.AddWithValue("posting_date", postingDate);
        command.Parameters.AddWithValue("quantity_delta", quantityDelta);
        command.Parameters.AddWithValue("quantity_after", quantityAfter);
        command.Parameters.AddWithValue("cost_amount_delta_base", costDeltaBase);
        command.Parameters.AddWithValue("cost_amount_after_base", costAfterBase);
        command.Parameters.AddWithValue("memo", memo);
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdjustBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        decimal onHandDelta,
        decimal reservedDelta,
        decimal inTransitOutDelta,
        decimal inTransitInDelta,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into item_warehouse_balances (
              id, company_id, item_id, warehouse_id, on_hand_qty, reserved_qty, in_transit_out_qty, in_transit_in_qty, updated_at
            )
            values (
              gen_random_uuid(), @company_id, @item_id, @warehouse_id, @on_hand_delta, @reserved_delta, @in_transit_out_delta, @in_transit_in_delta, now()
            )
            on conflict (company_id, item_id, warehouse_id)
            do update
              set on_hand_qty = item_warehouse_balances.on_hand_qty + excluded.on_hand_qty,
                  reserved_qty = item_warehouse_balances.reserved_qty + excluded.reserved_qty,
                  in_transit_out_qty = item_warehouse_balances.in_transit_out_qty + excluded.in_transit_out_qty,
                  in_transit_in_qty = item_warehouse_balances.in_transit_in_qty + excluded.in_transit_in_qty,
                  updated_at = now();
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("on_hand_delta", onHandDelta);
        command.Parameters.AddWithValue("reserved_delta", reservedDelta);
        command.Parameters.AddWithValue("in_transit_out_delta", inTransitOutDelta);
        command.Parameters.AddWithValue("in_transit_in_delta", inTransitInDelta);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IssueCostComputation ConsumeFifo(
        IReadOnlyList<OpenCostLayer> layers,
        decimal issueQuantity,
        string itemName)
    {
        var remainingQuantity = issueQuantity;
        var totalCostBase = 0m;
        var consumptions = new List<IssueLayerConsumption>();

        foreach (var layer in layers)
        {
            if (remainingQuantity <= 0)
            {
                break;
            }

            var consumedQty = decimal.Min(layer.RemainingQty, remainingQuantity);
            if (consumedQty <= 0)
            {
                continue;
            }

            var consumedCost = consumedQty == layer.RemainingQty
                ? layer.RemainingCostBase
                : decimal.Round(consumedQty * layer.UnitCostBase, 6, MidpointRounding.AwayFromZero);
            consumedCost = decimal.Min(consumedCost, layer.RemainingCostBase);

            remainingQuantity = decimal.Round(remainingQuantity - consumedQty, 6, MidpointRounding.AwayFromZero);
            totalCostBase = decimal.Round(totalCostBase + consumedCost, 6, MidpointRounding.AwayFromZero);
            consumptions.Add(new IssueLayerConsumption(
                layer.Id,
                consumedQty,
                consumedCost,
                decimal.Round(layer.RemainingQty - consumedQty, 6, MidpointRounding.AwayFromZero),
                decimal.Round(layer.RemainingCostBase - consumedCost, 6, MidpointRounding.AwayFromZero)));
        }

        if (remainingQuantity > 0)
        {
            throw new InvalidOperationException($"Transfer ship cannot post for '{itemName}' because the current receipt layers do not cover the outbound quantity yet.");
        }

        return new IssueCostComputation(totalCostBase, consumptions);
    }

    private static IssueCostComputation ConsumeMovingAverage(
        IReadOnlyList<OpenCostLayer> layers,
        decimal issueQuantity,
        string itemName)
    {
        var totalRemainingQty = decimal.Round(layers.Sum(layer => layer.RemainingQty), 6, MidpointRounding.AwayFromZero);
        var totalRemainingCost = decimal.Round(layers.Sum(layer => layer.RemainingCostBase), 6, MidpointRounding.AwayFromZero);

        if (totalRemainingQty < issueQuantity || totalRemainingQty <= 0)
        {
            throw new InvalidOperationException($"Transfer ship cannot post for '{itemName}' because the current receipt layers do not cover the outbound quantity yet.");
        }

        var issueCostBase = decimal.Round(issueQuantity * (totalRemainingCost / totalRemainingQty), 6, MidpointRounding.AwayFromZero);
        var remainingIssueQty = issueQuantity;
        var remainingIssueCost = issueCostBase;
        var consumptions = new List<IssueLayerConsumption>();

        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (remainingIssueQty <= 0)
            {
                break;
            }

            decimal consumedQty;
            decimal consumedCost;
            if (index == layers.Count - 1)
            {
                consumedQty = remainingIssueQty;
                consumedCost = remainingIssueCost;
            }
            else
            {
                consumedQty = decimal.Round(issueQuantity * (layer.RemainingQty / totalRemainingQty), 6, MidpointRounding.AwayFromZero);
                consumedQty = decimal.Min(consumedQty, decimal.Min(layer.RemainingQty, remainingIssueQty));

                consumedCost = decimal.Round(issueCostBase * (layer.RemainingCostBase / totalRemainingCost), 6, MidpointRounding.AwayFromZero);
                consumedCost = decimal.Min(consumedCost, decimal.Min(layer.RemainingCostBase, remainingIssueCost));
            }

            if (consumedQty <= 0 && index != layers.Count - 1)
            {
                continue;
            }

            remainingIssueQty = decimal.Round(remainingIssueQty - consumedQty, 6, MidpointRounding.AwayFromZero);
            remainingIssueCost = decimal.Round(remainingIssueCost - consumedCost, 6, MidpointRounding.AwayFromZero);

            consumptions.Add(new IssueLayerConsumption(
                layer.Id,
                consumedQty,
                consumedCost,
                decimal.Round(layer.RemainingQty - consumedQty, 6, MidpointRounding.AwayFromZero),
                decimal.Round(layer.RemainingCostBase - consumedCost, 6, MidpointRounding.AwayFromZero)));
        }

        if (remainingIssueQty > 0)
        {
            throw new InvalidOperationException($"Transfer ship cannot post for '{itemName}' because the current receipt layers do not cover the outbound quantity yet.");
        }

        return new IssueCostComputation(issueCostBase, consumptions);
    }

    private static string BuildTransferNumber() =>
        $"TRF-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

    private static string BuildShipLedgerMemo(string transferNumber, int lineNo) =>
        $"{transferNumber} ship line {lineNo}";

    private static string BuildReceiveLedgerMemo(string transferNumber, int lineNo) =>
        $"{transferNumber} receive line {lineNo}";

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

    private sealed record InventoryTransferHeader(
        Guid Id,
        CompanyId CompanyId,
        string TransferNumber,
        string Status,
        Guid SourceWarehouseId,
        Guid DestinationWarehouseId,
        UserId RequestedByUserId,
        string? Memo,
        DateTimeOffset CreatedAt,
        DateTimeOffset? SubmittedAt,
        DateTimeOffset? ShippedAt,
        DateTimeOffset? ReceivedAt);

    private sealed record InventoryTransferLineRow(
        int LineNo,
        Guid ItemId,
        string UomCode,
        decimal Quantity,
        decimal BaseQuantity,
        string? Memo);

    private sealed record ItemWarehouseBalanceSnapshot(
        decimal OnHandQty,
        decimal ReservedQty,
        decimal InTransitOutQty,
        decimal InTransitInQty);

    private sealed record OpenCostLayer(
        Guid Id,
        decimal RemainingQty,
        decimal RemainingCostBase,
        decimal UnitCostBase,
        DateOnly LayerDate,
        DateTimeOffset CreatedAt);

    private sealed record IssueLayerConsumption(
        Guid CostLayerId,
        decimal ConsumedQty,
        decimal ConsumedCostBase,
        decimal RemainingQtyAfter,
        decimal RemainingCostAfter);

    private sealed record IssueCostComputation(
        decimal TotalCostBase,
        IReadOnlyList<IssueLayerConsumption> Consumptions);
}
