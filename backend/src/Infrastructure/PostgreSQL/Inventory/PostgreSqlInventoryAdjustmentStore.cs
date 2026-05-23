using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryAdjustmentStore : IInventoryAdjustmentStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly IInventoryAdjustmentGlPoster _glPoster;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryAdjustmentStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore,
        IInventoryAdjustmentGlPoster glPoster)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
        _glPoster = glPoster ?? throw new ArgumentNullException(nameof(glPoster));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _foundationStore.EnsureSchemaAsync(cancellationToken);
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: true);
    }

    public async Task<InventoryAdjustmentDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var summary = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, null, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, null, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, null, companyId, cancellationToken);
        var recentAdjustments = await LoadRecentAdjustmentsAsync(connection, null, companyId, cancellationToken);

        return new InventoryAdjustmentDashboard(
            companyId,
            baseCurrencyCode,
            summary.CostingPolicy,
            activeItems,
            activeWarehouses,
            recentAdjustments);
    }

    public async Task<InventoryAdjustmentSummary> PostAsync(
        InventoryAdjustmentPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // P0-3b-1 (AUDIT_2026-05-20 C3 closure): the subledger writes
        // below (qty + cost layers + balances + ledger entries) commit
        // together with the matching Dr/Cr GL journal entry on the
        // SAME tx so the sub-ledger × unit-cost product reconciles to
        // the GL Inventory Asset balance after every adjustment. The
        // GL poster (Posting/PostgreSqlInventoryAdjustmentGlPoster)
        // runs at the bottom of the try-block, before commit; its
        // failure cascades to the outer catch and rolls back every
        // write made here. Per Q2=A:
        //   Gain      → Dr Inventory Asset / Cr Inventory Adjustment
        //   Loss      → Dr Inventory Adjustment / Cr Inventory Asset
        //   WriteOff  → same as Loss.
        var foundationSummary = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, request.CompanyId, cancellationToken);
            var warehouse = await LoadWarehouseAsync(connection, transaction, request.CompanyId, request.WarehouseId, cancellationToken)
                ?? throw new InvalidOperationException("Adjustment warehouse must be active in this company.");
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
                    throw new InvalidOperationException("Each adjustment line must reference an active inventory item in this company.");
                }

                if (item.ItemKind != InventoryItemKind.Stock)
                {
                    throw new InvalidOperationException($"Inventory adjustment only supports stock items. '{item.Name}' is not a stock item.");
                }

                if (item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
                {
                    throw new InvalidOperationException($"Inventory adjustment currently supports only warehouse-managed stock items. '{item.Name}' is not configured on that path.");
                }

                if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Adjustment line UOM must match the stock UOM for '{item.Name}'.");
                }
            }

            if (request.AdjustmentKind == InventoryAdjustmentKind.WriteOff &&
                foundationSummary.CostingPolicy?.RequireWriteOffApproval == true)
            {
                throw new InvalidOperationException("Write-off is blocked because company policy currently requires approval before posting.");
            }

            var now = DateTimeOffset.UtcNow;
            var documentId = Guid.NewGuid();
            var documentType = ToDocumentType(request.AdjustmentKind);
            var documentNumber = BuildDocumentNumber(request.AdjustmentKind, request.PostingDate);
            var totalQuantity = 0m;
            var totalCostBase = 0m;

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
                    """
                    insert into inventory_documents (
                      id, company_id, document_type, status, movement_direction, posting_date, source_module, source_document_id, source_document_number, counterparty_id, memo, created_by_user_id, created_at, posted_at
                    )
                    values (
                      @id, @company_id, @document_type, 'posted', 'neutral', @posting_date, 'inventory_adjustment', null, @source_document_number, null, @memo, @created_by_user_id, @created_at, @posted_at
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", documentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("document_type", documentType);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", request.UserId.Value);
                insertDocumentCommand.Parameters.AddWithValue("created_at", now);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", now);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                var item = itemMap[line.ItemId];
                var lineDocumentId = Guid.NewGuid();
                var ledgerEntryId = Guid.NewGuid();
                var baseQuantity = decimal.Round(line.Quantity, 6, MidpointRounding.AwayFromZero);

                switch (request.AdjustmentKind)
                {
                    case InventoryAdjustmentKind.Gain:
                    {
                        var unitCostBase = decimal.Round(line.UnitCostBase ?? 0m, 6, MidpointRounding.AwayFromZero);
                        var extendedCostBase = decimal.Round(baseQuantity * unitCostBase, 6, MidpointRounding.AwayFromZero);
                        var balance = await LoadCurrentBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var costBalance = await LoadCurrentCostBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var quantityAfter = decimal.Round(balance.OnHandQty + baseQuantity, 6, MidpointRounding.AwayFromZero);
                        var costAfter = decimal.Round(costBalance + extendedCostBase, 6, MidpointRounding.AwayFromZero);

                        await InsertDocumentLineAsync(connection, transaction, request.CompanyId, lineDocumentId, documentId, line, request.WarehouseId, baseCurrencyCode, unitCostBase, extendedCostBase, cancellationToken);
                        await AdjustOnHandAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, baseQuantity, cancellationToken);
                        await InsertLedgerEntryAsync(connection, transaction, ledgerEntryId, request.CompanyId, line.ItemId, request.WarehouseId, documentId, lineDocumentId, documentType, request.PostingDate, baseQuantity, quantityAfter, extendedCostBase, costAfter, BuildLedgerMemo(documentNumber, line.LineNo), now, cancellationToken);
                        await InsertCostLayerAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, ledgerEntryId, documentId, request.PostingDate, baseQuantity, unitCostBase, extendedCostBase, now, cancellationToken);

                        totalQuantity = decimal.Round(totalQuantity + baseQuantity, 6, MidpointRounding.AwayFromZero);
                        totalCostBase = decimal.Round(totalCostBase + extendedCostBase, 6, MidpointRounding.AwayFromZero);
                        break;
                    }
                    case InventoryAdjustmentKind.Loss:
                    case InventoryAdjustmentKind.WriteOff:
                    {
                        var balance = await LoadCurrentBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var availableQuantity = decimal.Round(balance.OnHandQty - balance.ReservedQty, 6, MidpointRounding.AwayFromZero);
                        if (foundationSummary.CostingPolicy?.NegativeStockAllowed != true && availableQuantity < baseQuantity)
                        {
                            throw new InvalidOperationException($"Adjustment cannot post because '{item.Name}' only has {availableQuantity} available in the selected warehouse.");
                        }

                        var costLayers = await LoadOpenCostLayersAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var consumptionResult = item.DefaultCostingMethod switch
                        {
                            InventoryCostingMethod.Fifo => ConsumeFifo(costLayers, baseQuantity, item.Name),
                            InventoryCostingMethod.MovingAverage => ConsumeMovingAverage(costLayers, baseQuantity, item.Name),
                            _ => throw new InvalidOperationException($"Unsupported inventory costing method '{item.DefaultCostingMethod}'.")
                        };

                        var costBalance = await LoadCurrentCostBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var quantityAfter = decimal.Round(balance.OnHandQty - baseQuantity, 6, MidpointRounding.AwayFromZero);
                        var costAfter = decimal.Round(costBalance - consumptionResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
                        var unitCostBase = baseQuantity == 0 ? 0m : decimal.Round(consumptionResult.TotalCostBase / baseQuantity, 6, MidpointRounding.AwayFromZero);

                        await InsertDocumentLineAsync(connection, transaction, request.CompanyId, lineDocumentId, documentId, line, request.WarehouseId, baseCurrencyCode, unitCostBase, consumptionResult.TotalCostBase, cancellationToken);
                        await AdjustOnHandAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, -baseQuantity, cancellationToken);
                        await InsertLedgerEntryAsync(connection, transaction, ledgerEntryId, request.CompanyId, line.ItemId, request.WarehouseId, documentId, lineDocumentId, documentType, request.PostingDate, -baseQuantity, quantityAfter, -consumptionResult.TotalCostBase, costAfter, BuildLedgerMemo(documentNumber, line.LineNo), now, cancellationToken);

                        foreach (var consumption in consumptionResult.Consumptions)
                        {
                            await UpdateRemainingLayerAsync(connection, transaction, consumption, cancellationToken);
                            await InsertLayerConsumptionAsync(connection, transaction, request.CompanyId, ledgerEntryId, consumption, now, cancellationToken);
                        }

                        totalQuantity = decimal.Round(totalQuantity + baseQuantity, 6, MidpointRounding.AwayFromZero);
                        totalCostBase = decimal.Round(totalCostBase + consumptionResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unsupported inventory adjustment kind '{request.AdjustmentKind}'.");
                }
            }

            // GL leg — Q2=A. Runs on the same connection/tx as the
            // subledger writes above; if it throws (e.g. SystemRole
            // account not pinned, closed period), the outer catch
            // block rolls everything back. totalCostBase is the sum
            // of cost_delta_base on the inventory_ledger_entries rows
            // we just inserted, so the GL amount matches the subledger
            // by construction.
            await _glPoster.AppendAsync(
                connection,
                transaction,
                new InventoryAdjustmentGlPostingRequest(
                    CompanyId: request.CompanyId,
                    UserId: request.UserId,
                    InventoryDocumentId: documentId,
                    InventoryDocumentNumber: documentNumber,
                    PostingDate: request.PostingDate,
                    BaseCurrencyCode: baseCurrencyCode,
                    Kind: ToGlKind(request.AdjustmentKind),
                    TotalCostBase: totalCostBase),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new InventoryAdjustmentSummary(
                documentId,
                request.CompanyId,
                documentNumber,
                "posted",
                request.AdjustmentKind,
                request.PostingDate,
                warehouse.Id,
                warehouse.WarehouseCode,
                warehouse.Name,
                totalQuantity,
                totalCostBase,
                request.Lines.Count,
                now,
                null,
                now,
                request.Memo);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static InventoryAdjustmentGlKind ToGlKind(InventoryAdjustmentKind kind) => kind switch
    {
        InventoryAdjustmentKind.Gain => InventoryAdjustmentGlKind.Gain,
        InventoryAdjustmentKind.Loss => InventoryAdjustmentGlKind.Loss,
        InventoryAdjustmentKind.WriteOff => InventoryAdjustmentGlKind.WriteOff,
        _ => throw new InvalidOperationException($"Unsupported inventory adjustment kind '{kind}'.")
    };

    public async Task<InventoryAdjustmentSummary> RequestWriteOffAsync(
        InventoryWriteOffRequestPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, request.CompanyId, cancellationToken);
            var warehouse = await LoadWarehouseAsync(connection, transaction, request.CompanyId, request.WarehouseId, cancellationToken)
                ?? throw new InvalidOperationException("Write-off request warehouse must be active in this company.");
            var itemMap = await LoadItemMapAsync(
                connection,
                transaction,
                request.CompanyId,
                request.Lines.Select(line => line.ItemId).Distinct().ToArray(),
                cancellationToken);

            foreach (var line in request.Lines)
            {
                ValidateWarehouseManagedStockLine(itemMap, line, "Write-off request");
            }

            var now = DateTimeOffset.UtcNow;
            var documentId = Guid.NewGuid();
            var documentNumber = BuildDocumentNumber(InventoryAdjustmentKind.WriteOff, request.PostingDate);

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
                    """
                    insert into inventory_documents (
                      id, company_id, document_type, status, movement_direction, posting_date, source_module, source_document_id, source_document_number, counterparty_id, memo, created_by_user_id, created_at, posted_at
                    )
                    values (
                      @id, @company_id, 'inventory_write_off', 'submitted', 'neutral', @posting_date, 'inventory_adjustment', null, @source_document_number, null, @memo, @created_by_user_id, @created_at, null
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", documentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", request.UserId.Value);
                insertDocumentCommand.Parameters.AddWithValue("created_at", now);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                await InsertDocumentLineAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    Guid.NewGuid(),
                    documentId,
                    line,
                    request.WarehouseId,
                    baseCurrencyCode,
                    0m,
                    0m,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return await LoadAdjustmentSummaryAsync(connection, null, request.CompanyId, documentId, cancellationToken)
                ?? throw new InvalidOperationException("Write-off request could not be reloaded after save.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<InventoryAdjustmentSummary> ApproveWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var document = await LoadAdjustmentDocumentForUpdateAsync(connection, transaction, request.CompanyId, request.DocumentId, cancellationToken)
                ?? throw new InvalidOperationException("Write-off request was not found in this company.");

            if (document.AdjustmentKind != InventoryAdjustmentKind.WriteOff)
            {
                throw new InvalidOperationException("Only write-off requests can be approved through this lane.");
            }

            if (!string.Equals(document.StoredStatus, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only submitted write-off requests can be approved.");
            }

            if (document.ApprovedAt.HasValue)
            {
                throw new InvalidOperationException("This write-off request is already approved.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                update inventory_documents
                set approved_at = @approved_at,
                    approved_by_user_id = @approved_by_user_id
                where id = @id
                  and company_id = @company_id;
                """;
            command.Parameters.AddWithValue("id", request.DocumentId);
            command.Parameters.AddWithValue("company_id", request.CompanyId.Value);
            command.Parameters.AddWithValue("approved_at", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("approved_by_user_id", request.UserId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return await LoadAdjustmentSummaryAsync(connection, null, request.CompanyId, request.DocumentId, cancellationToken)
                ?? throw new InvalidOperationException("Approved write-off request could not be reloaded.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<InventoryAdjustmentSummary> PostApprovedWriteOffAsync(
        InventoryWriteOffApprovePostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // P0-3b-1 (AUDIT_2026-05-20 C3 closure): same atomicity story as
        // PostAsync above — qty + cost layers + balances mutate
        // together with a Dr Inventory Adjustment / Cr Inventory Asset
        // GL entry on the same tx. The GL poster runs at the bottom
        // of the try-block, before commit; failure rolls back every
        // subledger write made here. Approved write-off uses the same
        // JE shape as Adjustment Loss per Q2=A.
        var foundationSummary = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var document = await LoadAdjustmentDocumentForUpdateAsync(connection, transaction, request.CompanyId, request.DocumentId, cancellationToken)
                ?? throw new InvalidOperationException("Write-off request was not found in this company.");

            if (document.AdjustmentKind != InventoryAdjustmentKind.WriteOff)
            {
                throw new InvalidOperationException("Only write-off requests can be posted through this lane.");
            }

            if (!string.Equals(document.StoredStatus, "submitted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only submitted write-off requests can be posted.");
            }

            if (foundationSummary.CostingPolicy?.RequireWriteOffApproval == true &&
                !document.ApprovedAt.HasValue)
            {
                throw new InvalidOperationException("Write-off must be approved before posting under the current company policy.");
            }

            var lines = await LoadPendingWriteOffLinesAsync(connection, transaction, request.CompanyId, request.DocumentId, cancellationToken);
            if (lines.Count == 0)
            {
                throw new InvalidOperationException("Write-off request has no lines to post.");
            }

            var itemMap = await LoadItemMapAsync(
                connection,
                transaction,
                request.CompanyId,
                lines.Select(line => line.ItemId).Distinct().ToArray(),
                cancellationToken);

            var totalQuantity = 0m;
            var totalCostBase = 0m;
            var now = DateTimeOffset.UtcNow;

            foreach (var line in lines.OrderBy(line => line.LineNo))
            {
                if (!itemMap.TryGetValue(line.ItemId, out var item))
                {
                    throw new InvalidOperationException("Each write-off line must reference an active inventory item in this company.");
                }

                ValidateWarehouseManagedStockLine(itemMap, line.ToAdjustmentLineInput(), "Write-off post");

                var balance = await LoadCurrentBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, line.WarehouseId, cancellationToken);
                var availableQuantity = decimal.Round(balance.OnHandQty - balance.ReservedQty, 6, MidpointRounding.AwayFromZero);
                if (foundationSummary.CostingPolicy?.NegativeStockAllowed != true && availableQuantity < line.BaseQuantity)
                {
                    throw new InvalidOperationException($"Write-off cannot post because '{item.Name}' only has {availableQuantity} available in the selected warehouse.");
                }

                var costLayers = await LoadOpenCostLayersAsync(connection, transaction, request.CompanyId, line.ItemId, line.WarehouseId, cancellationToken);
                var consumptionResult = item.DefaultCostingMethod switch
                {
                    InventoryCostingMethod.Fifo => ConsumeFifo(costLayers, line.BaseQuantity, item.Name),
                    InventoryCostingMethod.MovingAverage => ConsumeMovingAverage(costLayers, line.BaseQuantity, item.Name),
                    _ => throw new InvalidOperationException($"Unsupported inventory costing method '{item.DefaultCostingMethod}'.")
                };

                var costBalance = await LoadCurrentCostBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, line.WarehouseId, cancellationToken);
                var quantityAfter = decimal.Round(balance.OnHandQty - line.BaseQuantity, 6, MidpointRounding.AwayFromZero);
                var costAfter = decimal.Round(costBalance - consumptionResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
                var unitCostBase = line.BaseQuantity == 0 ? 0m : decimal.Round(consumptionResult.TotalCostBase / line.BaseQuantity, 6, MidpointRounding.AwayFromZero);
                var ledgerEntryId = Guid.NewGuid();

                await UpdateDocumentLineCostsAsync(
                    connection,
                    transaction,
                    line.DocumentLineId,
                    unitCostBase,
                    consumptionResult.TotalCostBase,
                    cancellationToken);

                await AdjustOnHandAsync(connection, transaction, request.CompanyId, line.ItemId, line.WarehouseId, -line.BaseQuantity, cancellationToken);
                await InsertLedgerEntryAsync(
                    connection,
                    transaction,
                    ledgerEntryId,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    request.DocumentId,
                    line.DocumentLineId,
                    "inventory_write_off",
                    document.PostingDate,
                    -line.BaseQuantity,
                    quantityAfter,
                    -consumptionResult.TotalCostBase,
                    costAfter,
                    BuildLedgerMemo(document.DocumentNumber, line.LineNo),
                    now,
                    cancellationToken);

                foreach (var consumption in consumptionResult.Consumptions)
                {
                    await UpdateRemainingLayerAsync(connection, transaction, consumption, cancellationToken);
                    await InsertLayerConsumptionAsync(connection, transaction, request.CompanyId, ledgerEntryId, consumption, now, cancellationToken);
                }

                totalQuantity = decimal.Round(totalQuantity + line.BaseQuantity, 6, MidpointRounding.AwayFromZero);
                totalCostBase = decimal.Round(totalCostBase + consumptionResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    update inventory_documents
                    set status = 'posted',
                        posted_at = @posted_at,
                        posted_by_user_id = @posted_by_user_id
                    where id = @id
                      and company_id = @company_id;
                    """;
                command.Parameters.AddWithValue("id", request.DocumentId);
                command.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                command.Parameters.AddWithValue("posted_at", now);
                command.Parameters.AddWithValue("posted_by_user_id", request.UserId.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // GL leg — Q2=A: Approved Write-off → Dr Inventory Adjustment / Cr Inventory Asset.
            // Same atomicity story as PostAsync: the poster runs on the
            // inventory tx, so a GL failure rolls back qty + cost +
            // balances + document-status writes together. totalCostBase
            // is the sum of cost_delta_base on the inventory_ledger_entries
            // rows we just inserted.
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(
                connection, transaction, request.CompanyId, cancellationToken);
            await _glPoster.AppendAsync(
                connection,
                transaction,
                new InventoryAdjustmentGlPostingRequest(
                    CompanyId: request.CompanyId,
                    UserId: request.UserId,
                    InventoryDocumentId: request.DocumentId,
                    InventoryDocumentNumber: document.DocumentNumber,
                    PostingDate: document.PostingDate,
                    BaseCurrencyCode: baseCurrencyCode,
                    Kind: InventoryAdjustmentGlKind.WriteOff,
                    TotalCostBase: totalCostBase),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return await LoadAdjustmentSummaryAsync(connection, null, request.CompanyId, request.DocumentId, cancellationToken)
                ?? throw new InvalidOperationException("Posted write-off could not be reloaded.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
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

        throw new InvalidOperationException("Company base currency could not be resolved for inventory adjustment.");
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

    private static async Task<InventoryManagedWarehouseSummary?> LoadWarehouseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid warehouseId,
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
              and id = @warehouse_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryManagedWarehouseSummary(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("warehouse_code")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.IsDBNull(reader.GetOrdinal("description"))
                ? null
                : reader.GetString(reader.GetOrdinal("description")),
            reader.GetBoolean(reader.GetOrdinal("is_active")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
    }

    private static async Task<IReadOnlyList<InventoryAdjustmentSummary>> LoadRecentAdjustmentsAsync(
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
              d.id as document_id,
              d.company_id,
              coalesce(d.source_document_number, 'UNNUMBERED') as document_number,
              d.document_type,
              case
                when d.status = 'submitted' and d.approved_at is not null then 'approved'
                else d.status
              end as status,
              d.posting_date,
              l.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              coalesce(sum(l.extended_cost_base), 0) as total_cost_base,
              count(l.id) as line_count,
              d.created_at,
              d.approved_at,
              d.posted_at,
              d.memo
            from inventory_documents d
            inner join inventory_document_lines l
              on l.document_id = d.id
            inner join inventory_warehouses w
              on w.id = l.warehouse_id
            where d.company_id = @company_id
              and d.document_type in ('inventory_adjustment_gain', 'inventory_adjustment_loss', 'inventory_write_off')
            group by
              d.id,
              d.company_id,
              d.source_document_number,
              d.document_type,
              d.status,
              d.posting_date,
              l.warehouse_id,
              w.warehouse_code,
              w.name,
              d.created_at,
              d.posted_at,
              d.memo
            order by d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var results = new List<InventoryAdjustmentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new InventoryAdjustmentSummary(
                reader.GetGuid(reader.GetOrdinal("document_id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                ParseAdjustmentKind(reader.GetString(reader.GetOrdinal("document_type"))),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_cost_base")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("approved_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("approved_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return results;
    }

    private static async Task<InventoryAdjustmentSummary?> LoadAdjustmentSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              d.id as document_id,
              d.company_id,
              coalesce(d.source_document_number, 'UNNUMBERED') as document_number,
              d.document_type,
              case
                when d.status = 'submitted' and d.approved_at is not null then 'approved'
                else d.status
              end as status,
              d.posting_date,
              l.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              coalesce(sum(l.extended_cost_base), 0) as total_cost_base,
              count(l.id) as line_count,
              d.created_at,
              d.approved_at,
              d.posted_at,
              d.memo
            from inventory_documents d
            inner join inventory_document_lines l
              on l.document_id = d.id
            inner join inventory_warehouses w
              on w.id = l.warehouse_id
            where d.company_id = @company_id
              and d.id = @document_id
            group by
              d.id,
              d.company_id,
              d.source_document_number,
              d.document_type,
              d.status,
              d.posting_date,
              l.warehouse_id,
              w.warehouse_code,
              w.name,
              d.created_at,
              d.approved_at,
              d.posted_at,
              d.memo;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryAdjustmentSummary(
            reader.GetGuid(reader.GetOrdinal("document_id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("document_number")),
            reader.GetString(reader.GetOrdinal("status")),
            ParseAdjustmentKind(reader.GetString(reader.GetOrdinal("document_type"))),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
            reader.GetGuid(reader.GetOrdinal("warehouse_id")),
            reader.GetString(reader.GetOrdinal("warehouse_code")),
            reader.GetString(reader.GetOrdinal("warehouse_name")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_cost_base")),
            reader.GetInt32(reader.GetOrdinal("line_count")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("approved_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("approved_at")),
            reader.IsDBNull(reader.GetOrdinal("posted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
            reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo")));
    }

    private static async Task<AdjustmentDocumentSnapshot?> LoadAdjustmentDocumentForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              company_id,
              coalesce(source_document_number, 'UNNUMBERED') as document_number,
              document_type,
              status,
              posting_date,
              approved_at,
              posted_at
            from inventory_documents
            where company_id = @company_id
              and id = @document_id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AdjustmentDocumentSnapshot(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("document_number")),
            ParseAdjustmentKind(reader.GetString(reader.GetOrdinal("document_type"))),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
            reader.IsDBNull(reader.GetOrdinal("approved_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("approved_at")),
            reader.IsDBNull(reader.GetOrdinal("posted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")));
    }

    private static async Task<IReadOnlyList<PendingWriteOffLine>> LoadPendingWriteOffLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              id,
              line_no,
              item_id,
              warehouse_id,
              uom_code,
              base_quantity,
              reason_code,
              memo
            from inventory_document_lines
            where company_id = @company_id
              and document_id = @document_id
            order by line_no asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        var lines = new List<PendingWriteOffLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new PendingWriteOffLine(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetInt32(reader.GetOrdinal("line_no")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("base_quantity")),
                reader.IsDBNull(reader.GetOrdinal("reason_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("reason_code")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return lines;
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
        // P0-4 (C4): FOR UPDATE serializes concurrent adjustment posts so
        // two write-offs against the same balance cannot both pass the
        // negative-stock guard before either UPSERT-decrements.
        command.CommandText =
            """
            select
              coalesce(on_hand_qty, 0) as on_hand_qty,
              coalesce(reserved_qty, 0) as reserved_qty
            from item_warehouse_balances
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new ItemWarehouseBalanceSnapshot(
                reader.GetFieldValue<decimal>(reader.GetOrdinal("on_hand_qty")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("reserved_qty")));
        }

        return new ItemWarehouseBalanceSnapshot(0m, 0m);
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

    private static async Task InsertDocumentLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid lineDocumentId,
        Guid documentId,
        InventoryAdjustmentLineInput line,
        Guid warehouseId,
        string currencyCode,
        decimal unitCostBase,
        decimal extendedCostBase,
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
              @id, @company_id, @document_id, @line_no, @item_id, @warehouse_id, @uom_code, @quantity, @base_quantity, @currency_code, 1, @unit_cost_tx, @unit_cost_base, @extended_cost_base, @reason_code, @memo
            );
            """;
        command.Parameters.AddWithValue("id", lineDocumentId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("line_no", line.LineNo);
        command.Parameters.AddWithValue("item_id", line.ItemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("uom_code", line.UomCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("quantity", line.Quantity);
        command.Parameters.AddWithValue("base_quantity", decimal.Round(line.Quantity, 6, MidpointRounding.AwayFromZero));
        command.Parameters.AddWithValue("currency_code", currencyCode);
        command.Parameters.AddWithValue("unit_cost_tx", unitCostBase);
        command.Parameters.AddWithValue("unit_cost_base", unitCostBase);
        command.Parameters.AddWithValue("extended_cost_base", extendedCostBase);
        command.Parameters.AddWithValue("reason_code", ToDbValue(line.ReasonCode));
        command.Parameters.AddWithValue("memo", ToDbValue(line.Memo));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateDocumentLineCostsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentLineId,
        decimal unitCostBase,
        decimal extendedCostBase,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update inventory_document_lines
            set unit_cost_tx = @unit_cost_base,
                unit_cost_base = @unit_cost_base,
                extended_cost_base = @extended_cost_base
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", documentLineId);
        command.Parameters.AddWithValue("unit_cost_base", unitCostBase);
        command.Parameters.AddWithValue("extended_cost_base", extendedCostBase);
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
              @id, @company_id, @item_id, @warehouse_id, @document_id, @document_line_id, 'neutral', @movement_type, @posting_date, @quantity_delta, @quantity_after, @cost_amount_delta_base, @cost_amount_after_base, @memo, @created_at
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

    private static async Task InsertCostLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid itemId,
        Guid warehouseId,
        Guid sourceLedgerEntryId,
        Guid sourceDocumentId,
        DateOnly postingDate,
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
              id, company_id, item_id, warehouse_id, source_ledger_entry_id, source_document_id, layer_date, original_qty, remaining_qty, unit_cost_base, remaining_cost_base, created_at
            )
            values (
              gen_random_uuid(), @company_id, @item_id, @warehouse_id, @source_ledger_entry_id, @source_document_id, @layer_date, @original_qty, @remaining_qty, @unit_cost_base, @remaining_cost_base, @created_at
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("source_ledger_entry_id", sourceLedgerEntryId);
        command.Parameters.AddWithValue("source_document_id", sourceDocumentId);
        command.Parameters.AddWithValue("layer_date", postingDate);
        command.Parameters.AddWithValue("original_qty", quantity);
        command.Parameters.AddWithValue("remaining_qty", quantity);
        command.Parameters.AddWithValue("unit_cost_base", unitCostBase);
        command.Parameters.AddWithValue("remaining_cost_base", remainingCostBase);
        command.Parameters.AddWithValue("created_at", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdjustOnHandAsync(
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
              id, company_id, item_id, warehouse_id, on_hand_qty, reserved_qty, in_transit_out_qty, in_transit_in_qty, updated_at
            )
            values (
              gen_random_uuid(), @company_id, @item_id, @warehouse_id, @quantity_delta, 0, 0, 0, now()
            )
            on conflict (company_id, item_id, warehouse_id)
            do update
              set on_hand_qty = item_warehouse_balances.on_hand_qty + excluded.on_hand_qty,
                  updated_at = now();
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("quantity_delta", quantityDelta);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRemainingLayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IssueLayerConsumption consumption,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update inventory_cost_layers
            set remaining_qty = @remaining_qty,
                remaining_cost_base = @remaining_cost_base
            where id = @id;
            """;
        command.Parameters.AddWithValue("id", consumption.CostLayerId);
        command.Parameters.AddWithValue("remaining_qty", consumption.RemainingQtyAfter);
        command.Parameters.AddWithValue("remaining_cost_base", consumption.RemainingCostAfter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLayerConsumptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid issueLedgerEntryId,
        IssueLayerConsumption consumption,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into inventory_layer_consumptions (
              id, company_id, issue_ledger_entry_id, cost_layer_id, consumed_qty, consumed_cost_base, created_at
            )
            values (
              gen_random_uuid(), @company_id, @issue_ledger_entry_id, @cost_layer_id, @consumed_qty, @consumed_cost_base, @created_at
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("issue_ledger_entry_id", issueLedgerEntryId);
        command.Parameters.AddWithValue("cost_layer_id", consumption.CostLayerId);
        command.Parameters.AddWithValue("consumed_qty", consumption.ConsumedQty);
        command.Parameters.AddWithValue("consumed_cost_base", consumption.ConsumedCostBase);
        command.Parameters.AddWithValue("created_at", createdAt);
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
            throw new InvalidOperationException($"Inventory adjustment cannot post for '{itemName}' because the current receipt layers do not cover the outbound quantity yet.");
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
            throw new InvalidOperationException($"Inventory adjustment cannot post for '{itemName}' because the current receipt layers do not cover the outbound quantity yet.");
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
            throw new InvalidOperationException($"Inventory adjustment cannot post for '{itemName}' because the current receipt layers do not cover the outbound quantity yet.");
        }

        return new IssueCostComputation(issueCostBase, consumptions);
    }

    private static string ToDocumentType(InventoryAdjustmentKind adjustmentKind) => adjustmentKind switch
    {
        InventoryAdjustmentKind.Gain => "inventory_adjustment_gain",
        InventoryAdjustmentKind.Loss => "inventory_adjustment_loss",
        InventoryAdjustmentKind.WriteOff => "inventory_write_off",
        _ => throw new InvalidOperationException($"Unsupported inventory adjustment kind '{adjustmentKind}'.")
    };

    private static InventoryAdjustmentKind ParseAdjustmentKind(string documentType) =>
        documentType.Trim().ToLowerInvariant() switch
        {
            "inventory_adjustment_gain" => InventoryAdjustmentKind.Gain,
            "inventory_adjustment_loss" => InventoryAdjustmentKind.Loss,
            "inventory_write_off" => InventoryAdjustmentKind.WriteOff,
            _ => throw new InvalidOperationException($"Unsupported inventory adjustment document type '{documentType}'.")
        };

    private static string BuildDocumentNumber(InventoryAdjustmentKind adjustmentKind, DateOnly postingDate) =>
        adjustmentKind switch
        {
            InventoryAdjustmentKind.Gain => $"IAG-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            InventoryAdjustmentKind.Loss => $"IAL-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            InventoryAdjustmentKind.WriteOff => $"IWO-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            _ => throw new InvalidOperationException($"Unsupported inventory adjustment kind '{adjustmentKind}'.")
        };

    private static string BuildLedgerMemo(string documentNumber, int lineNo) =>
        $"{documentNumber} line {lineNo}";

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

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

    private static void ValidateWarehouseManagedStockLine(
        IReadOnlyDictionary<Guid, InventoryManagedItemSummary> itemMap,
        InventoryAdjustmentLineInput line,
        string contextLabel)
    {
        if (!itemMap.TryGetValue(line.ItemId, out var item))
        {
            throw new InvalidOperationException($"Each {contextLabel.ToLowerInvariant()} line must reference an active inventory item in this company.");
        }

        if (item.ItemKind != InventoryItemKind.Stock)
        {
            throw new InvalidOperationException($"{contextLabel} only supports stock items. '{item.Name}' is not a stock item.");
        }

        if (item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
        {
            throw new InvalidOperationException($"{contextLabel} currently supports only warehouse-managed stock items. '{item.Name}' is not configured on that path.");
        }

        if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{contextLabel} line UOM must match the stock UOM for '{item.Name}'.");
        }
    }

    private async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken,
        bool allowCreate)
    {
        if (_schemaEnsured)
        {
            return;
        }

        if (await CoreSchemaExistsAsync(connection, cancellationToken))
        {
            _schemaEnsured = true;
            return;
        }

        if (!allowCreate)
        {
            throw new InvalidOperationException(
                "Inventory adjustment schema has not been installed. Apply database migrations before using inventory adjustments.");
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            if (await CoreSchemaExistsAsync(connection, cancellationToken))
            {
                _schemaEnsured = true;
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                alter table inventory_documents
                  add column if not exists approved_at timestamptz null;

                alter table inventory_documents
                  add column if not exists approved_by_user_id char(7) null;

                alter table inventory_documents
                  add column if not exists posted_by_user_id char(7) null;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task<bool> CoreSchemaExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              to_regclass('inventory_documents') is not null
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'approved_at')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'approved_by_user_id')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'posted_by_user_id');
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private sealed record ItemWarehouseBalanceSnapshot(
        decimal OnHandQty,
        decimal ReservedQty);

    private sealed record AdjustmentDocumentSnapshot(
        Guid DocumentId,
        CompanyId CompanyId,
        string DocumentNumber,
        InventoryAdjustmentKind AdjustmentKind,
        string StoredStatus,
        DateOnly PostingDate,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? PostedAt);

    private sealed record PendingWriteOffLine(
        Guid DocumentLineId,
        int LineNo,
        Guid ItemId,
        Guid WarehouseId,
        string UomCode,
        decimal BaseQuantity,
        string? ReasonCode,
        string? Memo)
    {
        public InventoryAdjustmentLineInput ToAdjustmentLineInput() =>
            new(LineNo, ItemId, UomCode, BaseQuantity, null, ReasonCode, Memo);
    }

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
