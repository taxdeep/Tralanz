using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryManufacturingStore : IInventoryManufacturingStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryManufacturingStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<InventoryManufacturingDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, null, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, null, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, null, companyId, cancellationToken);
        var boms = await LoadBomSummariesAsync(connection, null, companyId, activeItems, cancellationToken);
        var recentRuns = await LoadRecentRunsAsync(connection, null, companyId, cancellationToken);

        return new InventoryManufacturingDashboard(
            companyId,
            baseCurrencyCode,
            activeItems,
            activeWarehouses,
            boms,
            recentRuns);
    }

    public async Task<InventoryBomSummary> UpsertBomAsync(
        InventoryBomUpsertRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var itemIds = request.Components.Select(component => component.ComponentItemId)
                .Append(request.OutputItemId)
                .Distinct()
                .ToArray();
            var itemMap = await LoadItemMapAsync(connection, transaction, request.CompanyId, itemIds, cancellationToken);

            if (!itemMap.TryGetValue(request.OutputItemId, out var outputItem))
            {
                throw new InvalidOperationException("BOM output item must be an active inventory-managed stock item.");
            }

            ValidateBomItem(outputItem, "BOM output item");

            var seenLineNumbers = new HashSet<int>();
            var seenComponentIds = new HashSet<Guid>();
            foreach (var component in request.Components)
            {
                if (component.LineNo <= 0 || !seenLineNumbers.Add(component.LineNo))
                {
                    throw new InvalidOperationException("BOM line numbers must be positive and unique.");
                }

                if (!itemMap.TryGetValue(component.ComponentItemId, out var componentItem))
                {
                    throw new InvalidOperationException("Every BOM component must be an active inventory-managed stock item.");
                }

                ValidateBomItem(componentItem, $"BOM component '{componentItem.Name}'");

                if (component.ComponentItemId == request.OutputItemId)
                {
                    throw new InvalidOperationException("BOM output item cannot also appear as a component.");
                }

                if (!seenComponentIds.Add(component.ComponentItemId))
                {
                    throw new InvalidOperationException("BOM cannot contain the same component item more than once.");
                }

                if (component.Quantity <= 0)
                {
                    throw new InvalidOperationException("BOM component quantity must be greater than zero.");
                }

                if (component.WastagePercent < 0)
                {
                    throw new InvalidOperationException("BOM wastage percent cannot be negative.");
                }
            }

            var bomId = request.BomId ?? Guid.NewGuid();
            var updatedAt = DateTimeOffset.UtcNow;

            if (request.BomId.HasValue)
            {
                await EnsureBomExistsAsync(connection, transaction, request.CompanyId, bomId, cancellationToken);

                await using var updateBomCommand = connection.CreateCommand();
                updateBomCommand.Transaction = transaction;
                updateBomCommand.CommandText =
                    """
                    update boms
                    set bom_code = @bom_code,
                        output_item_id = @output_item_id,
                        output_qty = @output_qty,
                        is_active = @is_active,
                        updated_at = @updated_at
                    where id = @id
                      and company_id = @company_id;
                    """;
                updateBomCommand.Parameters.AddWithValue("id", bomId);
                updateBomCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                updateBomCommand.Parameters.AddWithValue("bom_code", request.BomCode.Trim().ToUpperInvariant());
                updateBomCommand.Parameters.AddWithValue("output_item_id", request.OutputItemId);
                updateBomCommand.Parameters.AddWithValue("output_qty", decimal.Round(request.OutputQuantity, 6, MidpointRounding.AwayFromZero));
                updateBomCommand.Parameters.AddWithValue("is_active", request.IsActive);
                updateBomCommand.Parameters.AddWithValue("updated_at", updatedAt);
                await updateBomCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var insertBomCommand = connection.CreateCommand();
                insertBomCommand.Transaction = transaction;
                insertBomCommand.CommandText =
                    """
                    insert into boms (
                      id,
                      company_id,
                      bom_code,
                      output_item_id,
                      output_qty,
                      is_active,
                      created_at,
                      updated_at
                    )
                    values (
                      @id,
                      @company_id,
                      @bom_code,
                      @output_item_id,
                      @output_qty,
                      @is_active,
                      @created_at,
                      @updated_at
                    );
                    """;
                insertBomCommand.Parameters.AddWithValue("id", bomId);
                insertBomCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                insertBomCommand.Parameters.AddWithValue("bom_code", request.BomCode.Trim().ToUpperInvariant());
                insertBomCommand.Parameters.AddWithValue("output_item_id", request.OutputItemId);
                insertBomCommand.Parameters.AddWithValue("output_qty", decimal.Round(request.OutputQuantity, 6, MidpointRounding.AwayFromZero));
                insertBomCommand.Parameters.AddWithValue("is_active", request.IsActive);
                insertBomCommand.Parameters.AddWithValue("created_at", updatedAt);
                insertBomCommand.Parameters.AddWithValue("updated_at", updatedAt);
                await insertBomCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteLinesCommand = connection.CreateCommand())
            {
                deleteLinesCommand.Transaction = transaction;
                deleteLinesCommand.CommandText =
                    """
                    delete from bom_lines
                    where bom_id = @bom_id;
                    """;
                deleteLinesCommand.Parameters.AddWithValue("bom_id", bomId);
                await deleteLinesCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var component in request.Components.OrderBy(component => component.LineNo))
            {
                await using var insertLineCommand = connection.CreateCommand();
                insertLineCommand.Transaction = transaction;
                insertLineCommand.CommandText =
                    """
                    insert into bom_lines (
                      id,
                      company_id,
                      bom_id,
                      line_no,
                      component_item_id,
                      quantity,
                      wastage_percent,
                      memo
                    )
                    values (
                      gen_random_uuid(),
                      @company_id,
                      @bom_id,
                      @line_no,
                      @component_item_id,
                      @quantity,
                      @wastage_percent,
                      @memo
                    );
                    """;
                insertLineCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                insertLineCommand.Parameters.AddWithValue("bom_id", bomId);
                insertLineCommand.Parameters.AddWithValue("line_no", component.LineNo);
                insertLineCommand.Parameters.AddWithValue("component_item_id", component.ComponentItemId);
                insertLineCommand.Parameters.AddWithValue("quantity", decimal.Round(component.Quantity, 6, MidpointRounding.AwayFromZero));
                insertLineCommand.Parameters.AddWithValue("wastage_percent", decimal.Round(component.WastagePercent, 4, MidpointRounding.AwayFromZero));
                insertLineCommand.Parameters.AddWithValue("memo", ToDbValue(component.Memo));
                await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            var bom = await LoadBomSummaryByIdAsync(connection, null, request.CompanyId, bomId, itemMap.Values.ToArray(), cancellationToken);
            return bom ?? throw new InvalidOperationException("Saved BOM could not be reloaded.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another manufacturing BOM already uses the same company-scoped BOM code.", ex);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<InventoryManufacturingSummary> PostAsync(
        InventoryManufacturingPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var foundationSummary = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, request.CompanyId, cancellationToken);
            var warehouseMap = await LoadWarehouseMapAsync(connection, transaction, request.CompanyId, new[] { request.WarehouseId }, cancellationToken);
            if (!warehouseMap.TryGetValue(request.WarehouseId, out var warehouse))
            {
                throw new InvalidOperationException("Manufacturing must post into an active warehouse.");
            }

            var bom = await LoadBomRecordAsync(connection, transaction, request.CompanyId, request.BomId, cancellationToken);
            if (bom is null)
            {
                throw new InvalidOperationException("Selected BOM does not exist in this company.");
            }

            if (!bom.IsActive)
            {
                throw new InvalidOperationException("Selected BOM is inactive and cannot post manufacturing.");
            }

            var itemIds = bom.Components.Select(component => component.ComponentItemId)
                .Append(bom.OutputItemId)
                .Distinct()
                .ToArray();
            var itemMap = await LoadItemMapAsync(connection, transaction, request.CompanyId, itemIds, cancellationToken);
            if (!itemMap.TryGetValue(bom.OutputItemId, out var outputItem))
            {
                throw new InvalidOperationException("Manufacturing output item must stay active.");
            }

            ValidateBomItem(outputItem, "Manufacturing output item");
            foreach (var component in bom.Components)
            {
                if (!itemMap.TryGetValue(component.ComponentItemId, out var componentItem))
                {
                    throw new InvalidOperationException("Manufacturing component item must stay active.");
                }

                ValidateBomItem(componentItem, $"Manufacturing component '{componentItem.Name}'");
            }

            var runId = Guid.NewGuid();
            var runNumber = BuildRunNumber(request.PostingDate);
            var issueDocumentId = Guid.NewGuid();
            var issueDocumentNumber = BuildIssueNumber(request.PostingDate);
            var receiptDocumentId = Guid.NewGuid();
            var receiptDocumentNumber = BuildReceiptNumber(request.PostingDate);
            var createdAt = DateTimeOffset.UtcNow;
            var normalizedOutputQuantity = decimal.Round(request.OutputQuantity, 6, MidpointRounding.AwayFromZero);
            var negativeStockAllowed = foundationSummary.CostingPolicy?.NegativeStockAllowed == true;
            var totalConsumedCostBase = 0m;
            var issueLineNo = 0;

            await InsertInventoryDocumentAsync(connection, transaction, issueDocumentId, request.CompanyId, issueDocumentNumber, "manufacturing_issue", "outbound", request.PostingDate, "inventory.manufacturing", runId, runNumber, request.Memo, request.UserId, createdAt, cancellationToken);

            foreach (var component in bom.Components.OrderBy(component => component.LineNo))
            {
                issueLineNo++;
                var componentItem = itemMap[component.ComponentItemId];
                var requiredQuantity = CalculateRequiredQuantity(component, bom.OutputQuantity, normalizedOutputQuantity);
                var balance = await LoadCurrentBalanceAsync(connection, transaction, request.CompanyId, component.ComponentItemId, request.WarehouseId, cancellationToken);
                var availableQuantity = decimal.Round(balance.OnHandQty - balance.ReservedQty, 6, MidpointRounding.AwayFromZero);

                if (!negativeStockAllowed && availableQuantity < requiredQuantity)
                {
                    throw new InvalidOperationException($"Manufacturing cannot post because '{componentItem.Name}' only has {availableQuantity} available in the selected warehouse.");
                }

                if (negativeStockAllowed && availableQuantity < requiredQuantity && componentItem.BackorderMode == InventoryBackorderMode.Disallow)
                {
                    throw new InvalidOperationException($"Manufacturing cannot push '{componentItem.Name}' negative because the item backorder mode is set to disallow.");
                }

                var costLayers = await LoadOpenCostLayersAsync(connection, transaction, request.CompanyId, component.ComponentItemId, request.WarehouseId, cancellationToken);
                var issueCostResult = componentItem.DefaultCostingMethod switch
                {
                    InventoryCostingMethod.Fifo => ConsumeFifo(costLayers, requiredQuantity, componentItem.Name),
                    InventoryCostingMethod.MovingAverage => ConsumeMovingAverage(costLayers, requiredQuantity, componentItem.Name),
                    _ => throw new InvalidOperationException($"Unsupported inventory costing method '{componentItem.DefaultCostingMethod}'.")
                };

                var quantityAfter = decimal.Round(balance.OnHandQty - requiredQuantity, 6, MidpointRounding.AwayFromZero);
                var currentCostBalance = await LoadCurrentCostBalanceAsync(connection, transaction, request.CompanyId, component.ComponentItemId, request.WarehouseId, cancellationToken);
                var costAfter = decimal.Round(currentCostBalance - issueCostResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
                var issueLineId = Guid.NewGuid();
                var issueLedgerEntryId = Guid.NewGuid();
                var unitCostBase = requiredQuantity == 0 ? 0m : decimal.Round(issueCostResult.TotalCostBase / requiredQuantity, 6, MidpointRounding.AwayFromZero);

                await InsertDocumentLineAsync(connection, transaction, issueLineId, request.CompanyId, issueDocumentId, issueLineNo, component.ComponentItemId, request.WarehouseId, componentItem.StockUomCode ?? "EA", requiredQuantity, baseCurrencyCode, 1m, unitCostBase, unitCostBase, issueCostResult.TotalCostBase, "bom_component", component.Memo, cancellationToken);
                await UpsertOnHandBalanceAsync(connection, transaction, request.CompanyId, component.ComponentItemId, request.WarehouseId, -requiredQuantity, cancellationToken);
                await InsertLedgerEntryAsync(connection, transaction, issueLedgerEntryId, request.CompanyId, component.ComponentItemId, request.WarehouseId, issueDocumentId, issueLineId, "outbound", "manufacturing_issue", request.PostingDate, -requiredQuantity, quantityAfter, -issueCostResult.TotalCostBase, costAfter, BuildLedgerMemo(issueDocumentNumber, issueLineNo), createdAt, cancellationToken);
                await ApplyLayerConsumptionsAsync(connection, transaction, request.CompanyId, issueLedgerEntryId, issueCostResult.Consumptions, createdAt, cancellationToken);

                totalConsumedCostBase = decimal.Round(totalConsumedCostBase + issueCostResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
            }

            await InsertInventoryDocumentAsync(connection, transaction, receiptDocumentId, request.CompanyId, receiptDocumentNumber, "manufacturing_receipt", "inbound", request.PostingDate, "inventory.manufacturing", runId, runNumber, request.Memo, request.UserId, createdAt, cancellationToken);

            var receiptLineId = Guid.NewGuid();
            var receiptLedgerEntryId = Guid.NewGuid();
            var costLayerId = Guid.NewGuid();
            var outputQuantityAfter = await LoadCurrentOnHandAsync(connection, transaction, request.CompanyId, bom.OutputItemId, request.WarehouseId, cancellationToken);
            outputQuantityAfter = decimal.Round(outputQuantityAfter + normalizedOutputQuantity, 6, MidpointRounding.AwayFromZero);
            var outputCostBalance = await LoadCurrentCostBalanceAsync(connection, transaction, request.CompanyId, bom.OutputItemId, request.WarehouseId, cancellationToken);
            var outputCostAfter = decimal.Round(outputCostBalance + totalConsumedCostBase, 6, MidpointRounding.AwayFromZero);
            var outputUnitCostBase = normalizedOutputQuantity == 0 ? 0m : decimal.Round(totalConsumedCostBase / normalizedOutputQuantity, 6, MidpointRounding.AwayFromZero);

            await InsertDocumentLineAsync(connection, transaction, receiptLineId, request.CompanyId, receiptDocumentId, 1, bom.OutputItemId, request.WarehouseId, outputItem.StockUomCode ?? "EA", normalizedOutputQuantity, baseCurrencyCode, 1m, outputUnitCostBase, outputUnitCostBase, totalConsumedCostBase, "manufacturing_output", request.Memo, cancellationToken);
            await UpsertOnHandBalanceAsync(connection, transaction, request.CompanyId, bom.OutputItemId, request.WarehouseId, normalizedOutputQuantity, cancellationToken);
            await InsertLedgerEntryAsync(connection, transaction, receiptLedgerEntryId, request.CompanyId, bom.OutputItemId, request.WarehouseId, receiptDocumentId, receiptLineId, "inbound", "manufacturing_receipt", request.PostingDate, normalizedOutputQuantity, outputQuantityAfter, totalConsumedCostBase, outputCostAfter, BuildLedgerMemo(receiptDocumentNumber, 1), createdAt, cancellationToken);
            await InsertCostLayerAsync(connection, transaction, costLayerId, request.CompanyId, bom.OutputItemId, request.WarehouseId, receiptDocumentId, receiptLineId, receiptLedgerEntryId, request.PostingDate, normalizedOutputQuantity, outputUnitCostBase, totalConsumedCostBase, createdAt, cancellationToken);

            var summary = new InventoryManufacturingSummary(runId, request.CompanyId, runNumber, bom.BomId, bom.BomCode, bom.OutputItemId, itemMap[bom.OutputItemId].ItemCode, itemMap[bom.OutputItemId].Name, warehouse.Id, warehouse.WarehouseCode, warehouse.Name, normalizedOutputQuantity, totalConsumedCostBase, outputUnitCostBase, issueDocumentNumber, receiptDocumentNumber, createdAt, NormalizeOptionalText(request.Memo));

            await transaction.CommitAsync(cancellationToken);
            return summary;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another manufacturing run already uses the same generated company-scoped number.", ex);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

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
                alter table inventory_documents
                  add column if not exists document_number text null;

                create unique index if not exists ux_inventory_documents_company_document_number
                  on inventory_documents (company_id, lower(document_number))
                  where document_number is not null;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static void ValidateBomItem(
        InventoryManagedItemSummary item,
        string contextLabel)
    {
        if (item.ItemKind != InventoryItemKind.Stock)
        {
            throw new InvalidOperationException($"{contextLabel} must be a stock item.");
        }

        if (item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
        {
            throw new InvalidOperationException($"{contextLabel} must be warehouse-managed stock.");
        }
    }

    private static decimal CalculateRequiredQuantity(
        InventoryBomComponentInput component,
        decimal bomOutputQuantity,
        decimal requestedOutputQuantity)
    {
        var normalizedBomOutputQuantity = decimal.Round(bomOutputQuantity, 6, MidpointRounding.AwayFromZero);
        var normalizedRequestedOutputQuantity = decimal.Round(requestedOutputQuantity, 6, MidpointRounding.AwayFromZero);
        var normalizedComponentQuantity = decimal.Round(component.Quantity, 6, MidpointRounding.AwayFromZero);
        var withWastage = decimal.Round(
            normalizedComponentQuantity * (1 + (component.WastagePercent / 100m)),
            6,
            MidpointRounding.AwayFromZero);
        return normalizedBomOutputQuantity == 0
            ? 0m
            : decimal.Round(normalizedRequestedOutputQuantity * (withWastage / normalizedBomOutputQuantity), 6, MidpointRounding.AwayFromZero);
    }

    private static async Task EnsureBomExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid bomId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select exists(
              select 1
              from boms
              where id = @bom_id
                and company_id = @company_id
            );
            """;
        command.Parameters.AddWithValue("bom_id", bomId);
        command.Parameters.AddWithValue("company_id", companyId);

        var exists = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!exists)
        {
            throw new InvalidOperationException("Selected BOM does not exist in this company.");
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
        command.Parameters.AddWithValue("company_id", companyId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string baseCurrencyCode || string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            throw new InvalidOperationException("Company base currency is required before manufacturing can run.");
        }

        return baseCurrencyCode.Trim().ToUpperInvariant();
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
        command.Parameters.AddWithValue("company_id", companyId);

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

    private static async Task<Dictionary<Guid, InventoryManagedItemSummary>> LoadItemMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, InventoryManagedItemSummary>();
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
              and id = any(@item_ids)
              and is_active = true;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
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
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
            items[item.Id] = item;
        }

        return items;
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
        command.Parameters.AddWithValue("company_id", companyId);

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

    private static async Task<Dictionary<Guid, InventoryManagedWarehouseSummary>> LoadWarehouseMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> warehouseIds,
        CancellationToken cancellationToken)
    {
        if (warehouseIds.Count == 0)
        {
            return new Dictionary<Guid, InventoryManagedWarehouseSummary>();
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
        command.Parameters.AddWithValue("company_id", companyId);
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
                reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
            warehouses[warehouse.Id] = warehouse;
        }

        return warehouses;
    }

    private static async Task<IReadOnlyList<InventoryBomSummary>> LoadBomSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyList<InventoryManagedItemSummary> activeItems,
        CancellationToken cancellationToken)
    {
        var itemMap = activeItems.ToDictionary(item => item.Id);
        var bomRows = new List<BomRow>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                select
                  b.id,
                  b.company_id,
                  b.bom_code,
                  b.output_item_id,
                  b.output_qty,
                  b.is_active,
                  b.updated_at
                from boms b
                where b.company_id = @company_id
                order by b.bom_code asc;
                """;
            command.Parameters.AddWithValue("company_id", companyId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bomRows.Add(new BomRow(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                    reader.GetString(reader.GetOrdinal("bom_code")),
                    reader.GetGuid(reader.GetOrdinal("output_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("output_qty")),
                    reader.GetBoolean(reader.GetOrdinal("is_active")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
            }
        }

        if (bomRows.Count == 0)
        {
            return [];
        }

        var bomIds = bomRows.Select(row => row.BomId).ToArray();
        var componentMap = await LoadBomComponentMapAsync(connection, transaction, companyId, bomIds, cancellationToken);
        var rollupMap = await LoadBomCostRollupMapAsync(connection, transaction, companyId, bomRows, componentMap, cancellationToken);

        var summaries = new List<InventoryBomSummary>();
        foreach (var bom in bomRows)
        {
            if (!itemMap.TryGetValue(bom.OutputItemId, out var outputItem))
            {
                continue;
            }

            summaries.Add(new InventoryBomSummary(
                bom.BomId,
                bom.CompanyId,
                bom.BomCode,
                bom.OutputItemId,
                outputItem.ItemCode,
                outputItem.Name,
                outputItem.StockUomCode ?? "EA",
                bom.OutputQuantity,
                bom.IsActive,
                bom.UpdatedAt,
                rollupMap[bom.BomId],
                componentMap.GetValueOrDefault(bom.BomId, [])));
        }

        return summaries;
    }

    private static async Task<InventoryBomSummary?> LoadBomSummaryByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid bomId,
        IReadOnlyList<InventoryManagedItemSummary> activeItems,
        CancellationToken cancellationToken)
    {
        var all = await LoadBomSummariesAsync(connection, transaction, companyId, activeItems, cancellationToken);
        return all.FirstOrDefault(summary => summary.BomId == bomId);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<InventoryBomComponentInput>>> LoadBomComponentMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> bomIds,
        CancellationToken cancellationToken)
    {
        var components = new Dictionary<Guid, List<InventoryBomComponentInput>>();
        if (bomIds.Count == 0)
        {
            return components.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<InventoryBomComponentInput>)entry.Value);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              bom_id,
              line_no,
              component_item_id,
              quantity,
              wastage_percent,
              memo
            from bom_lines
            where company_id = @company_id
              and bom_id = any(@bom_ids)
            order by bom_id asc, line_no asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bom_ids", bomIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bomId = reader.GetGuid(reader.GetOrdinal("bom_id"));
            if (!components.TryGetValue(bomId, out var lines))
            {
                lines = [];
                components[bomId] = lines;
            }

            lines.Add(new InventoryBomComponentInput(
                reader.GetInt32(reader.GetOrdinal("line_no")),
                reader.GetGuid(reader.GetOrdinal("component_item_id")),
                reader.GetDecimal(reader.GetOrdinal("quantity")),
                reader.GetDecimal(reader.GetOrdinal("wastage_percent")),
                reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return components.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<InventoryBomComponentInput>)entry.Value);
    }

    private static async Task<Dictionary<Guid, InventoryBomCostRollupSummary>> LoadBomCostRollupMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyList<BomRow> boms,
        IReadOnlyDictionary<Guid, IReadOnlyList<InventoryBomComponentInput>> componentMap,
        CancellationToken cancellationToken)
    {
        var componentIds = componentMap.Values
            .SelectMany(components => components.Select(component => component.ComponentItemId))
            .Distinct()
            .ToArray();
        var averageCostMap = await LoadAverageCostMapAsync(connection, transaction, companyId, componentIds, cancellationToken);
        var results = new Dictionary<Guid, InventoryBomCostRollupSummary>();

        foreach (var bom in boms)
        {
            var totalCost = 0m;
            var isComplete = true;
            foreach (var component in componentMap.GetValueOrDefault(bom.BomId, []))
            {
                if (!averageCostMap.TryGetValue(component.ComponentItemId, out var averageCost))
                {
                    isComplete = false;
                    continue;
                }

                var requiredQuantity = CalculateRequiredQuantity(component, bom.OutputQuantity, bom.OutputQuantity);
                totalCost = decimal.Round(totalCost + (requiredQuantity * averageCost), 6, MidpointRounding.AwayFromZero);
            }

            var unitCost = bom.OutputQuantity == 0
                ? 0m
                : decimal.Round(totalCost / bom.OutputQuantity, 6, MidpointRounding.AwayFromZero);
            var note = isComplete
                ? null
                : "Estimated cost is incomplete because one or more component items do not yet have open cost layers.";
            results[bom.BomId] = new InventoryBomCostRollupSummary(totalCost, unitCost, isComplete, note);
        }

        return results;
    }

    private static async Task<Dictionary<Guid, decimal>> LoadAverageCostMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, decimal>();
        if (itemIds.Count == 0)
        {
            return map;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              item_id,
              coalesce(sum(remaining_qty), 0) as remaining_qty,
              coalesce(sum(remaining_cost_base), 0) as remaining_cost_base
            from inventory_cost_layers
            where company_id = @company_id
              and item_id = any(@item_ids)
              and remaining_qty > 0
            group by item_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_ids", itemIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = reader.GetGuid(reader.GetOrdinal("item_id"));
            var remainingQty = reader.GetDecimal(reader.GetOrdinal("remaining_qty"));
            var remainingCost = reader.GetDecimal(reader.GetOrdinal("remaining_cost_base"));
            if (remainingQty > 0)
            {
                map[itemId] = decimal.Round(remainingCost / remainingQty, 6, MidpointRounding.AwayFromZero);
            }
        }

        return map;
    }

    private static async Task<BomRecord?> LoadBomRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid bomId,
        CancellationToken cancellationToken)
    {
        BomRow? bomRow = null;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                select
                  id,
                  company_id,
                  bom_code,
                  output_item_id,
                  output_qty,
                  is_active,
                  updated_at
                from boms
                where company_id = @company_id
                  and id = @bom_id;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("bom_id", bomId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                bomRow = new BomRow(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                    reader.GetString(reader.GetOrdinal("bom_code")),
                    reader.GetGuid(reader.GetOrdinal("output_item_id")),
                    reader.GetDecimal(reader.GetOrdinal("output_qty")),
                    reader.GetBoolean(reader.GetOrdinal("is_active")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
            }
        }

        if (bomRow is null)
        {
            return null;
        }

        var componentMap = await LoadBomComponentMapAsync(connection, transaction, companyId, new[] { bomId }, cancellationToken);
        return new BomRecord(
            bomRow.BomId,
            bomRow.CompanyId,
            bomRow.BomCode,
            bomRow.OutputItemId,
            bomRow.OutputQuantity,
            bomRow.IsActive,
            bomRow.UpdatedAt,
            componentMap.GetValueOrDefault(bomId, []));
    }

    private static async Task<IReadOnlyList<InventoryManufacturingSummary>> LoadRecentRunsAsync(
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
              r.source_document_id as run_id,
              r.company_id,
              coalesce(r.source_document_number, r.document_number, 'UNNUMBERED') as run_number,
              r.document_number as receipt_document_number,
              coalesce(iss.document_number, 'UNNUMBERED') as issue_document_number,
              split_part(coalesce(r.source_document_number, ''), '-', 1) as run_prefix,
              rline.item_id as output_item_id,
              i.item_code as output_item_code,
              i.name as output_item_name,
              rline.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              rline.base_quantity as output_quantity,
              rline.extended_cost_base as total_consumed_cost_base,
              rline.unit_cost_base,
              r.posted_at,
              r.memo
            from inventory_documents r
            inner join inventory_document_lines rline
              on rline.document_id = r.id
            inner join inventory_items i
              on i.id = rline.item_id
            inner join inventory_warehouses w
              on w.id = rline.warehouse_id
            left join inventory_documents iss
              on iss.source_document_id = r.source_document_id
             and iss.document_type = 'manufacturing_issue'
            where r.company_id = @company_id
              and r.document_type = 'manufacturing_receipt'
            order by r.posting_date desc, r.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var summaries = new List<InventoryManufacturingSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var runNumber = reader.GetString(reader.GetOrdinal("run_number"));
            summaries.Add(new InventoryManufacturingSummary(
                reader.IsDBNull(reader.GetOrdinal("run_id")) ? Guid.Empty : reader.GetGuid(reader.GetOrdinal("run_id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                runNumber,
                Guid.Empty,
                "BOM",
                reader.GetGuid(reader.GetOrdinal("output_item_id")),
                reader.GetString(reader.GetOrdinal("output_item_code")),
                reader.GetString(reader.GetOrdinal("output_item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetDecimal(reader.GetOrdinal("output_quantity")),
                reader.GetDecimal(reader.GetOrdinal("total_consumed_cost_base")),
                reader.GetDecimal(reader.GetOrdinal("unit_cost_base")),
                reader.GetString(reader.GetOrdinal("issue_document_number")),
                reader.GetString(reader.GetOrdinal("receipt_document_number")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("memo")) ? null : TrimMemoSuffix(reader.GetString(reader.GetOrdinal("memo")))));
        }

        return summaries;
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
              coalesce(on_hand_qty, 0),
              coalesce(reserved_qty, 0),
              coalesce(in_transit_out_qty, 0),
              coalesce(in_transit_in_qty, 0)
            from item_warehouse_balances
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new ItemWarehouseBalanceSnapshot(
                reader.GetDecimal(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3));
        }

        return new ItemWarehouseBalanceSnapshot(0m, 0m, 0m, 0m);
    }

    private static async Task<decimal> LoadCurrentOnHandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        var balance = await LoadCurrentBalanceAsync(connection, transaction, companyId, itemId, warehouseId, cancellationToken);
        return balance.OnHandQty;
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
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
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
              unit_cost_base,
              remaining_cost_base,
              layer_date,
              created_at
            from inventory_cost_layers
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id
              and remaining_qty > 0
            order by layer_date asc, created_at asc, id asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        var layers = new List<OpenCostLayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            layers.Add(new OpenCostLayer(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetDecimal(reader.GetOrdinal("remaining_qty")),
                reader.GetDecimal(reader.GetOrdinal("unit_cost_base")),
                reader.GetDecimal(reader.GetOrdinal("remaining_cost_base")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("layer_date")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return layers;
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
            throw new InvalidOperationException($"Manufacturing cannot post for '{itemName}' because the current receipt layers do not cover the required component quantity.");
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
            throw new InvalidOperationException($"Manufacturing cannot post for '{itemName}' because the current receipt layers do not cover the required component quantity.");
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
            throw new InvalidOperationException($"Manufacturing cannot post for '{itemName}' because the current receipt layers do not cover the required component quantity.");
        }

        return new IssueCostComputation(issueCostBase, consumptions);
    }

    private static async Task UpsertOnHandBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        decimal quantityDelta,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into item_warehouse_balances (
              id,
              company_id,
              item_id,
              warehouse_id,
              on_hand_qty,
              reserved_qty,
              in_transit_out_qty,
              in_transit_in_qty,
              updated_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @item_id,
              @warehouse_id,
              @quantity_delta,
              0,
              0,
              0,
              now()
            )
            on conflict (company_id, item_id, warehouse_id)
            do update
              set on_hand_qty = item_warehouse_balances.on_hand_qty + excluded.on_hand_qty,
                  updated_at = now();
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("quantity_delta", quantityDelta);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInventoryDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CompanyId companyId,
        string documentNumber,
        string documentType,
        string movementDirection,
        DateOnly postingDate,
        string sourceModule,
        Guid runId,
        string runNumber,
        string? memo,
        UserId userId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_documents (
              id,
              company_id,
              document_number,
              document_type,
              status,
              movement_direction,
              posting_date,
              source_module,
              source_document_id,
              source_document_number,
              counterparty_id,
              memo,
              created_by_user_id,
              created_at,
              posted_at
            )
            values (
              @id,
              @company_id,
              @document_number,
              @document_type,
              'posted',
              @movement_direction,
              @posting_date,
              @source_module,
              @source_document_id,
              @source_document_number,
              null,
              @memo,
              @created_by_user_id,
              @created_at,
              @posted_at
            );
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_number", documentNumber);
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("movement_direction", movementDirection);
        command.Parameters.AddWithValue("posting_date", postingDate);
        command.Parameters.AddWithValue("source_module", sourceModule);
        command.Parameters.AddWithValue("source_document_id", runId);
        command.Parameters.AddWithValue("source_document_number", runNumber);
        command.Parameters.AddWithValue("memo", ToDbValue(BuildDocumentMemo(memo, runNumber)));
        command.Parameters.AddWithValue("created_by_user_id", userId);
        command.Parameters.AddWithValue("created_at", createdAt);
        command.Parameters.AddWithValue("posted_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDocumentLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid lineId,
        CompanyId companyId,
        Guid documentId,
        int lineNo,
        Guid itemId,
        Guid warehouseId,
        string uomCode,
        decimal quantity,
        string currencyCode,
        decimal fxRateToBase,
        decimal unitCostTx,
        decimal unitCostBase,
        decimal extendedCostBase,
        string reasonCode,
        string? memo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_document_lines (
              id,
              company_id,
              document_id,
              line_no,
              item_id,
              warehouse_id,
              uom_code,
              quantity,
              base_quantity,
              currency_code,
              fx_rate_to_base,
              unit_cost_tx,
              unit_cost_base,
              extended_cost_base,
              reason_code,
              memo
            )
            values (
              @id,
              @company_id,
              @document_id,
              @line_no,
              @item_id,
              @warehouse_id,
              @uom_code,
              @quantity,
              @base_quantity,
              @currency_code,
              @fx_rate_to_base,
              @unit_cost_tx,
              @unit_cost_base,
              @extended_cost_base,
              @reason_code,
              @memo
            );
            """;
        command.Parameters.AddWithValue("id", lineId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("line_no", lineNo);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("uom_code", uomCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("base_quantity", decimal.Round(quantity, 6, MidpointRounding.AwayFromZero));
        command.Parameters.AddWithValue("currency_code", currencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("fx_rate_to_base", fxRateToBase);
        command.Parameters.AddWithValue("unit_cost_tx", unitCostTx);
        command.Parameters.AddWithValue("unit_cost_base", unitCostBase);
        command.Parameters.AddWithValue("extended_cost_base", extendedCostBase);
        command.Parameters.AddWithValue("reason_code", reasonCode);
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
        string movementDirection,
        string movementType,
        DateOnly postingDate,
        decimal quantityDelta,
        decimal quantityAfter,
        decimal costAmountDeltaBase,
        decimal costAmountAfterBase,
        string memo,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_ledger_entries (
              id,
              company_id,
              item_id,
              warehouse_id,
              document_id,
              document_line_id,
              movement_direction,
              movement_type,
              posting_date,
              quantity_delta,
              quantity_after,
              cost_amount_delta_base,
              cost_amount_after_base,
              memo,
              created_at
            )
            values (
              @id,
              @company_id,
              @item_id,
              @warehouse_id,
              @document_id,
              @document_line_id,
              @movement_direction,
              @movement_type,
              @posting_date,
              @quantity_delta,
              @quantity_after,
              @cost_amount_delta_base,
              @cost_amount_after_base,
              @memo,
              @created_at
            );
            """;
        command.Parameters.AddWithValue("id", ledgerEntryId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("document_line_id", documentLineId);
        command.Parameters.AddWithValue("movement_direction", movementDirection);
        command.Parameters.AddWithValue("movement_type", movementType);
        command.Parameters.AddWithValue("posting_date", postingDate);
        command.Parameters.AddWithValue("quantity_delta", quantityDelta);
        command.Parameters.AddWithValue("quantity_after", quantityAfter);
        command.Parameters.AddWithValue("cost_amount_delta_base", costAmountDeltaBase);
        command.Parameters.AddWithValue("cost_amount_after_base", costAmountAfterBase);
        command.Parameters.AddWithValue("memo", memo);
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyLayerConsumptionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid issueLedgerEntryId,
        IReadOnlyList<IssueLayerConsumption> consumptions,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        foreach (var consumption in consumptions)
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
                  id,
                  company_id,
                  issue_ledger_entry_id,
                  cost_layer_id,
                  consumed_qty,
                  consumed_cost_base,
                  created_at
                )
                values (
                  gen_random_uuid(),
                  @company_id,
                  @issue_ledger_entry_id,
                  @cost_layer_id,
                  @consumed_qty,
                  @consumed_cost_base,
                  @created_at
                );
                """;
            insertConsumptionCommand.Parameters.AddWithValue("company_id", companyId);
            insertConsumptionCommand.Parameters.AddWithValue("issue_ledger_entry_id", issueLedgerEntryId);
            insertConsumptionCommand.Parameters.AddWithValue("cost_layer_id", consumption.CostLayerId);
            insertConsumptionCommand.Parameters.AddWithValue("consumed_qty", consumption.ConsumedQty);
            insertConsumptionCommand.Parameters.AddWithValue("consumed_cost_base", consumption.ConsumedCostBase);
            insertConsumptionCommand.Parameters.AddWithValue("created_at", createdAt);
            await insertConsumptionCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertCostLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid costLayerId,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        Guid sourceDocumentId,
        Guid sourceDocumentLineId,
        Guid sourceLedgerEntryId,
        DateOnly layerDate,
        decimal quantity,
        decimal unitCostBase,
        decimal remainingCostBase,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_cost_layers (
              id,
              company_id,
              item_id,
              warehouse_id,
              source_document_id,
              source_document_line_id,
              source_ledger_entry_id,
              layer_date,
              original_qty,
              remaining_qty,
              unit_cost_base,
              remaining_cost_base,
              created_at
            )
            values (
              @id,
              @company_id,
              @item_id,
              @warehouse_id,
              @source_document_id,
              @source_document_line_id,
              @source_ledger_entry_id,
              @layer_date,
              @original_qty,
              @remaining_qty,
              @unit_cost_base,
              @remaining_cost_base,
              @created_at
            );
            """;
        command.Parameters.AddWithValue("id", costLayerId);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("source_document_id", sourceDocumentId);
        command.Parameters.AddWithValue("source_document_line_id", sourceDocumentLineId);
        command.Parameters.AddWithValue("source_ledger_entry_id", sourceLedgerEntryId);
        command.Parameters.AddWithValue("layer_date", layerDate);
        command.Parameters.AddWithValue("original_qty", quantity);
        command.Parameters.AddWithValue("remaining_qty", quantity);
        command.Parameters.AddWithValue("unit_cost_base", unitCostBase);
        command.Parameters.AddWithValue("remaining_cost_base", remainingCostBase);
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildRunNumber(DateOnly postingDate) =>
        $"MFG-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static string BuildIssueNumber(DateOnly postingDate) =>
        $"MFGI-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static string BuildReceiptNumber(DateOnly postingDate) =>
        $"MFGR-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static string BuildLedgerMemo(string documentNumber, int lineNo) =>
        $"Inventory manufacturing {documentNumber} line {lineNo}";

    private static string? BuildDocumentMemo(string? memo, string runNumber)
    {
        var normalizedMemo = NormalizeOptionalText(memo);
        return string.IsNullOrWhiteSpace(normalizedMemo)
            ? $"run:{runNumber}"
            : $"{normalizedMemo} |run:{runNumber}";
    }

    private static string? TrimMemoSuffix(string memo)
    {
        var separatorIndex = memo.LastIndexOf("|run:", StringComparison.OrdinalIgnoreCase);
        if (separatorIndex <= 0)
        {
            return memo;
        }

        return memo[..separatorIndex].Trim();
    }

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InventoryItemKind ParseItemKind(string value) => value switch
    {
        "stock" => InventoryItemKind.Stock,
        "service" => InventoryItemKind.Service,
        "drop_ship" => InventoryItemKind.DropShip,
        _ => InventoryItemKind.NonStock
    };

    private static ManageInventoryMethod ParseManageInventoryMethod(string value) => value switch
    {
        "manage_stock" => ManageInventoryMethod.ManageStock,
        "manage_stock_by_sku" => ManageInventoryMethod.ManageStockBySku,
        _ => ManageInventoryMethod.DontManageStock
    };

    private static InventoryCostingMethod ParseCostingMethod(string value) => value switch
    {
        "fifo" => InventoryCostingMethod.Fifo,
        _ => InventoryCostingMethod.MovingAverage
    };

    private static InventoryBackorderMode ParseBackorderMode(string value) => value switch
    {
        "allow_negative" => InventoryBackorderMode.AllowNegative,
        "allow_negative_with_warning" => InventoryBackorderMode.AllowNegativeWithWarning,
        _ => InventoryBackorderMode.Disallow
    };

    private static InventoryLowStockActivity ParseLowStockActivity(string value) => value switch
    {
        "warn" => InventoryLowStockActivity.Warn,
        "block_issue" => InventoryLowStockActivity.BlockOutbound,
        _ => InventoryLowStockActivity.Nothing
    };

    private sealed record BomRow(
        Guid BomId,
        CompanyId CompanyId,
        string BomCode,
        Guid OutputItemId,
        decimal OutputQuantity,
        bool IsActive,
        DateTimeOffset UpdatedAt);

    private sealed record BomRecord(
        Guid BomId,
        CompanyId CompanyId,
        string BomCode,
        Guid OutputItemId,
        decimal OutputQuantity,
        bool IsActive,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<InventoryBomComponentInput> Components);

    private sealed record ItemWarehouseBalanceSnapshot(
        decimal OnHandQty,
        decimal ReservedQty,
        decimal InTransitOutQty,
        decimal InTransitInQty);

    private sealed record OpenCostLayer(
        Guid Id,
        decimal RemainingQty,
        decimal UnitCostBase,
        decimal RemainingCostBase,
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
