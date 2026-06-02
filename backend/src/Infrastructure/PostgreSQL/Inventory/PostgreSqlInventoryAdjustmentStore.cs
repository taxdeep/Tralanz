using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Infrastructure.PostgreSQL.Numbering;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryAdjustmentStore : IInventoryAdjustmentStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryAdjustmentStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
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

        var foundationSummary = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, request.CompanyId, cancellationToken);
            await EnsurePostingPeriodOpenAsync(connection, transaction, request.CompanyId, request.PostingDate, cancellationToken);

            var clientRequestHash = request.ClientRequestId.HasValue
                ? BuildPostRequestHash(request)
                : null;

            if (request.ClientRequestId.HasValue)
            {
                await AcquireInventoryDocumentMutationLockAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    request.ClientRequestId.Value,
                    cancellationToken);

                var existing = await TryLoadIdempotentAdjustmentAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    request.ClientRequestId.Value,
                    request.AdjustmentKind,
                    requiredStatus: "posted",
                    expectedClientRequestHash: clientRequestHash,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return existing;
                }
            }

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
            var documentId = request.ClientRequestId ?? Guid.NewGuid();
            var documentType = ToDocumentType(request.AdjustmentKind);
            var documentNumber = BuildDocumentNumber(request.AdjustmentKind, request.PostingDate);
            var totalQuantity = 0m;
            var totalCostBase = 0m;
            var journalCandidates = new List<InventoryAdjustmentJournalCandidate>();

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
                    """
                    insert into inventory_documents (
                      id, company_id, document_number, document_type, status, movement_direction, posting_date, source_module, source_document_id, source_document_number, counterparty_id, memo, client_request_hash, created_by_user_id, created_at, posted_at
                    )
                    values (
                      @id, @company_id, @document_number, @document_type, 'posted', 'neutral', @posting_date, 'inventory_adjustment', null, @source_document_number, null, @memo, @client_request_hash, @created_by_user_id, @created_at, @posted_at
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", documentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("document_type", documentType);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("client_request_hash", ToDbValue(clientRequestHash));
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
                        var inventoryAssetAccountId = await ResolveInventoryAssetAccountIdAsync(
                            connection,
                            transaction,
                            request.CompanyId,
                            item,
                            cancellationToken);
                        var adjustmentAccountId = await ResolveInventoryAdjustmentAccountIdAsync(
                            connection,
                            transaction,
                            request.CompanyId,
                            item,
                            preferItemWriteOffAccount: false,
                            cancellationToken);
                        var balance = await LoadCurrentBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var costBalance = await LoadCurrentCostBalanceAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, cancellationToken);
                        var quantityAfter = decimal.Round(balance.OnHandQty + baseQuantity, 6, MidpointRounding.AwayFromZero);
                        var costAfter = decimal.Round(costBalance + extendedCostBase, 6, MidpointRounding.AwayFromZero);

                        await InsertDocumentLineAsync(connection, transaction, request.CompanyId, lineDocumentId, documentId, line, request.WarehouseId, baseCurrencyCode, unitCostBase, extendedCostBase, cancellationToken);
                        await AdjustOnHandAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, baseQuantity, cancellationToken);
                        await InsertLedgerEntryAsync(connection, transaction, ledgerEntryId, request.CompanyId, line.ItemId, request.WarehouseId, documentId, lineDocumentId, documentType, request.PostingDate, baseQuantity, quantityAfter, extendedCostBase, costAfter, BuildLedgerMemo(documentNumber, line.LineNo), now, cancellationToken);
                        await InsertCostLayerAsync(connection, transaction, request.CompanyId, line.ItemId, request.WarehouseId, ledgerEntryId, documentId, request.PostingDate, baseQuantity, unitCostBase, extendedCostBase, now, cancellationToken);

                        journalCandidates.Add(new InventoryAdjustmentJournalCandidate(
                            line.LineNo,
                            item.ItemCode,
                            inventoryAssetAccountId,
                            adjustmentAccountId,
                            extendedCostBase));

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

                        var inventoryAssetAccountId = await ResolveInventoryAssetAccountIdAsync(
                            connection,
                            transaction,
                            request.CompanyId,
                            item,
                            cancellationToken);
                        var adjustmentAccountId = await ResolveInventoryAdjustmentAccountIdAsync(
                            connection,
                            transaction,
                            request.CompanyId,
                            item,
                            preferItemWriteOffAccount: request.AdjustmentKind == InventoryAdjustmentKind.WriteOff,
                            cancellationToken);
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

                        journalCandidates.Add(new InventoryAdjustmentJournalCandidate(
                            line.LineNo,
                            item.ItemCode,
                            inventoryAssetAccountId,
                            adjustmentAccountId,
                            consumptionResult.TotalCostBase));

                        totalQuantity = decimal.Round(totalQuantity + baseQuantity, 6, MidpointRounding.AwayFromZero);
                        totalCostBase = decimal.Round(totalCostBase + consumptionResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"Unsupported inventory adjustment kind '{request.AdjustmentKind}'.");
                }
            }

            var journalEntryId = await InsertInventoryAdjustmentJournalAsync(
                connection,
                transaction,
                request.CompanyId,
                request.UserId,
                documentId,
                documentType,
                documentNumber,
                request.AdjustmentKind,
                request.PostingDate,
                baseCurrencyCode,
                journalCandidates,
                cancellationToken);
            await InsertInventoryAdjustmentAuditAsync(
                connection,
                transaction,
                request.CompanyId,
                request.UserId,
                documentId,
                "inventory_adjustment_posted",
                documentType,
                documentNumber,
                request.PostingDate,
                totalQuantity,
                totalCostBase,
                journalEntryId,
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
            var clientRequestHash = request.ClientRequestId.HasValue
                ? BuildWriteOffRequestHash(request)
                : null;

            if (request.ClientRequestId.HasValue)
            {
                await AcquireInventoryDocumentMutationLockAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    request.ClientRequestId.Value,
                    cancellationToken);

                var existing = await TryLoadIdempotentAdjustmentAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    request.ClientRequestId.Value,
                    InventoryAdjustmentKind.WriteOff,
                    requiredStatus: null,
                    expectedClientRequestHash: clientRequestHash,
                    cancellationToken);
                if (existing is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return existing;
                }
            }

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
            var documentId = request.ClientRequestId ?? Guid.NewGuid();
            var documentNumber = BuildDocumentNumber(InventoryAdjustmentKind.WriteOff, request.PostingDate);

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
                    """
                    insert into inventory_documents (
                      id, company_id, document_number, document_type, status, movement_direction, posting_date, source_module, source_document_id, source_document_number, counterparty_id, memo, client_request_hash, created_by_user_id, created_at, posted_at
                    )
                    values (
                      @id, @company_id, @document_number, 'inventory_write_off', 'submitted', 'neutral', @posting_date, 'inventory_adjustment', null, @source_document_number, null, @memo, @client_request_hash, @created_by_user_id, @created_at, null
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", documentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("client_request_hash", ToDbValue(clientRequestHash));
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
                await transaction.CommitAsync(cancellationToken);
                return await LoadAdjustmentSummaryAsync(connection, null, request.CompanyId, request.DocumentId, cancellationToken)
                    ?? throw new InvalidOperationException("Approved write-off request could not be reloaded.");
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

            if (string.Equals(document.StoredStatus, "posted", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.CommitAsync(cancellationToken);
                return await LoadAdjustmentSummaryAsync(connection, null, request.CompanyId, request.DocumentId, cancellationToken)
                    ?? throw new InvalidOperationException("Posted write-off could not be reloaded.");
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

            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, request.CompanyId, cancellationToken);
            await EnsurePostingPeriodOpenAsync(connection, transaction, request.CompanyId, document.PostingDate, cancellationToken);

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
            var journalCandidates = new List<InventoryAdjustmentJournalCandidate>();

            foreach (var line in lines.OrderBy(line => line.LineNo))
            {
                if (!itemMap.TryGetValue(line.ItemId, out var item))
                {
                    throw new InvalidOperationException("Each write-off line must reference an active inventory item in this company.");
                }

                ValidateWarehouseManagedStockLine(itemMap, line.ToAdjustmentLineInput(), "Write-off post");

                var inventoryAssetAccountId = await ResolveInventoryAssetAccountIdAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    item,
                    cancellationToken);
                var adjustmentAccountId = await ResolveInventoryAdjustmentAccountIdAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    item,
                    preferItemWriteOffAccount: true,
                    cancellationToken);
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

                journalCandidates.Add(new InventoryAdjustmentJournalCandidate(
                    line.LineNo,
                    item.ItemCode,
                    inventoryAssetAccountId,
                    adjustmentAccountId,
                    consumptionResult.TotalCostBase));

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

            var journalEntryId = await InsertInventoryAdjustmentJournalAsync(
                connection,
                transaction,
                request.CompanyId,
                request.UserId,
                request.DocumentId,
                "inventory_write_off",
                document.DocumentNumber,
                InventoryAdjustmentKind.WriteOff,
                document.PostingDate,
                baseCurrencyCode,
                journalCandidates,
                cancellationToken);
            await InsertInventoryAdjustmentAuditAsync(
                connection,
                transaction,
                request.CompanyId,
                request.UserId,
                request.DocumentId,
                "inventory_write_off_posted",
                "inventory_write_off",
                document.DocumentNumber,
                document.PostingDate,
                totalQuantity,
                totalCostBase,
                journalEntryId,
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
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
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
              d.document_number,
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
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
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
              d.document_number,
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

    private static async Task<InventoryAdjustmentSummary?> TryLoadIdempotentAdjustmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        InventoryAdjustmentKind expectedAdjustmentKind,
        string? requiredStatus,
        string? expectedClientRequestHash,
        CancellationToken cancellationToken)
    {
        var existing = await LoadAdjustmentSummaryAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.AdjustmentKind != expectedAdjustmentKind)
        {
            throw new InvalidOperationException(
                "Client request id already belongs to a different inventory adjustment kind.");
        }

        var existingClientRequestHash = await LoadClientRequestHashAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingClientRequestHash) &&
            !string.IsNullOrWhiteSpace(expectedClientRequestHash) &&
            !string.Equals(existingClientRequestHash, expectedClientRequestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Client request id already belongs to a different inventory request payload.");
        }

        if (!string.IsNullOrWhiteSpace(requiredStatus) &&
            !string.Equals(existing.Status, requiredStatus, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Client request id already belongs to an inventory adjustment in status '{existing.Status}'.");
        }

        return existing;
    }

    private static async Task<string?> LoadClientRequestHashAsync(
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
            select client_request_hash
            from inventory_documents
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task AcquireInventoryDocumentMutationLockAsync(
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
            select pg_advisory_xact_lock(
              hashtext(@company_id),
              hashtext(@document_id)
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
              coalesce(document_number, source_document_number, 'UNNUMBERED') as document_number,
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
        command.CommandText =
            """
            select
              coalesce(on_hand_qty, 0) as on_hand_qty,
              coalesce(reserved_qty, 0) as reserved_qty
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

    private static async Task<Guid?> InsertInventoryAdjustmentJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        string sourceType,
        string documentNumber,
        InventoryAdjustmentKind adjustmentKind,
        DateOnly postingDate,
        string baseCurrencyCode,
        IReadOnlyList<InventoryAdjustmentJournalCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var lines = InventoryAdjustmentJournalPlan.Build(adjustmentKind, documentNumber, candidates);
        if (lines.Count == 0)
        {
            return null;
        }

        var journalEntryId = Guid.NewGuid();
        var totalDebit = Round6(lines.Sum(static line => line.Debit));
        var totalCredit = Round6(lines.Sum(static line => line.Credit));
        if (totalDebit != totalCredit)
        {
            throw new InvalidOperationException("Inventory adjustment journal entry is not balanced.");
        }

        var postedAt = DateTimeOffset.UtcNow;
        var journalDisplayNumber = await ReserveJournalDisplayNumberAsync(connection, transaction, companyId, cancellationToken);
        var entityNumber = await ReserveEntityNumberAsync(connection, transaction, companyId, postingDate.Year, cancellationToken);
        var idempotencyKey = $"{sourceType}:{documentId:D}";

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into journal_entries (
                  id, company_id, entity_number, display_number, status,
                  source_type, source_id,
                  transaction_currency_code, base_currency_code,
                  exchange_rate, exchange_rate_date, exchange_rate_source,
                  fx_rate_snapshot_id,
                  total_tx_debit, total_tx_credit, total_debit, total_credit,
                  posting_run_id, idempotency_key, posted_at, created_by_user_id, created_at
                )
                values (
                  @id, @company_id, @entity_number, @display_number, 'posted',
                  @source_type, @source_id,
                  @base_currency_code, @base_currency_code,
                  1, @posting_date, 'identity',
                  null,
                  @total_debit, @total_credit, @total_debit, @total_credit,
                  @posting_run_id, @idempotency_key, @posted_at, @created_by_user_id, now()
                )
                on conflict (company_id, idempotency_key) do nothing;
                """;
            command.Parameters.AddWithValue("id", journalEntryId);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("display_number", journalDisplayNumber);
            command.Parameters.AddWithValue("source_type", sourceType);
            command.Parameters.AddWithValue("source_id", documentId);
            command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            command.Parameters.Add(new NpgsqlParameter("posting_date", NpgsqlDbType.Date) { Value = postingDate });
            command.Parameters.AddWithValue("total_debit", totalDebit);
            command.Parameters.AddWithValue("total_credit", totalCredit);
            command.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            command.Parameters.AddWithValue("posted_at", postedAt);
            command.Parameters.AddWithValue("created_by_user_id", userId.Value);

            var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken);
            if (insertedRows != 1)
            {
                return await ReadExistingJournalEntryIdAsync(connection, transaction, companyId, idempotencyKey, cancellationToken);
            }
        }

        var lineNumber = 1;
        foreach (var line in lines)
        {
            await InsertJournalAndLedgerLineAsync(
                connection,
                transaction,
                companyId,
                journalEntryId,
                lineNumber++,
                line.AccountId,
                line.Description,
                baseCurrencyCode,
                line.TxDebit,
                line.TxCredit,
                line.Debit,
                line.Credit,
                postingDate,
                line.PostingRole,
                line.SourceLineNumber,
                cancellationToken);
        }

        return journalEntryId;
    }

    private static async Task InsertJournalAndLedgerLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        int lineNumber,
        Guid accountId,
        string description,
        string transactionCurrencyCode,
        decimal txDebit,
        decimal txCredit,
        decimal debit,
        decimal credit,
        DateOnly postingDate,
        string postingRole,
        int sourceLineNumber,
        CancellationToken cancellationToken)
    {
        var journalEntryLineId = Guid.NewGuid();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into journal_entry_lines (
                  id, company_id, journal_entry_id, line_number,
                  account_id, description, party_type, party_id,
                  tx_debit, tx_credit, debit, credit,
                  tax_component_type, control_role, posting_role, source_line_number,
                  created_at
                )
                values (
                  @id, @company_id, @journal_entry_id, @line_number,
                  @account_id, @description, null, null,
                  @tx_debit, @tx_credit, @debit, @credit,
                  null, null, @posting_role, @source_line_number,
                  now()
                );
                """;
            command.Parameters.AddWithValue("id", journalEntryLineId);
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            command.Parameters.AddWithValue("line_number", lineNumber);
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.AddWithValue("description", description);
            command.Parameters.AddWithValue("tx_debit", Round6(txDebit));
            command.Parameters.AddWithValue("tx_credit", Round6(txCredit));
            command.Parameters.AddWithValue("debit", Round6(debit));
            command.Parameters.AddWithValue("credit", Round6(credit));
            command.Parameters.AddWithValue("posting_role", postingRole);
            command.Parameters.AddWithValue("source_line_number", sourceLineNumber);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var ledgerCommand = connection.CreateCommand();
        ledgerCommand.Transaction = transaction;
        ledgerCommand.CommandText =
            """
            insert into ledger_entries (
              id, company_id, journal_entry_id, journal_entry_line_id,
              posting_date, account_id, debit, credit,
              transaction_currency_code, tx_debit, tx_credit,
              created_at
            )
            values (
              @id, @company_id, @journal_entry_id, @journal_entry_line_id,
              @posting_date, @account_id, @debit, @credit,
              @transaction_currency_code, @tx_debit, @tx_credit,
              now()
            );
            """;
        ledgerCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        ledgerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        ledgerCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        ledgerCommand.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
        ledgerCommand.Parameters.Add(new NpgsqlParameter("posting_date", NpgsqlDbType.Date) { Value = postingDate });
        ledgerCommand.Parameters.AddWithValue("account_id", accountId);
        ledgerCommand.Parameters.AddWithValue("debit", Round6(debit));
        ledgerCommand.Parameters.AddWithValue("credit", Round6(credit));
        ledgerCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        ledgerCommand.Parameters.AddWithValue("tx_debit", Round6(txDebit));
        ledgerCommand.Parameters.AddWithValue("tx_credit", Round6(txCredit));
        await ledgerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> ResolveInventoryAssetAccountIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        InventoryManagedItemSummary item,
        CancellationToken cancellationToken)
    {
        return await ResolveActiveAccountIdAsync(
            connection,
            transaction,
            companyId,
            item.DefaultInventoryAssetAccountId,
            "inventory asset",
            cancellationToken,
            InventoryAdjustmentAccountPolicy.InventoryAssetRootTypes,
            "inventory_asset",
            "inventory:asset");
    }

    private static async Task<Guid> ResolveInventoryAdjustmentAccountIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        InventoryManagedItemSummary item,
        bool preferItemWriteOffAccount,
        CancellationToken cancellationToken)
    {
        return await ResolveActiveAccountIdAsync(
            connection,
            transaction,
            companyId,
            preferItemWriteOffAccount ? item.DefaultWriteOffAccountId : null,
            preferItemWriteOffAccount ? "inventory write-off" : "inventory adjustment",
            cancellationToken,
            InventoryAdjustmentAccountPolicy.AdjustmentOffsetRootTypes,
            "inventory_adjustment",
            "inventory:adjustment");
    }

    private static async Task<Guid> ResolveActiveAccountIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid? preferredAccountId,
        string accountLabel,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string> allowedRootTypes,
        params string[] systemMarkers)
    {
        if (preferredAccountId.HasValue)
        {
            var preferredAccount = await LoadActiveAccountAsync(
                connection,
                transaction,
                companyId,
                preferredAccountId.Value,
                cancellationToken);
            if (preferredAccount is null)
            {
                throw new InvalidOperationException(
                    $"The configured {accountLabel} account is not active in this company.");
            }

            if (InventoryAdjustmentAccountPolicy.AllowsRootType(preferredAccount.RootType, allowedRootTypes))
            {
                return preferredAccountId.Value;
            }

            throw new InvalidOperationException(
                $"The configured {accountLabel} account '{preferredAccount.Code} - {preferredAccount.Name}' has root type '{preferredAccount.RootType}', but inventory adjustment posting requires {InventoryAdjustmentAccountPolicy.FormatAllowedRootTypes(allowedRootTypes)}.");
        }

        var fallback = await TryResolveSystemAccountIdAsync(
            connection,
            transaction,
            companyId,
            cancellationToken,
            allowedRootTypes,
            systemMarkers);
        if (fallback.HasValue)
        {
            return fallback.Value;
        }

        throw new InvalidOperationException(
            $"Inventory adjustment posting requires an active {accountLabel} account with root type {InventoryAdjustmentAccountPolicy.FormatAllowedRootTypes(allowedRootTypes)}. Configure an item default or a company account with system role/key '{string.Join("' or '", systemMarkers)}'.");
    }

    private static async Task<InventoryAdjustmentAccountSnapshot?> LoadActiveAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, code, name, root_type
            from accounts
            where company_id = @company_id
              and id = @account_id
              and is_active = true;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InventoryAdjustmentAccountSnapshot(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("code")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("root_type")));
    }

    private static async Task<Guid?> TryResolveSystemAccountIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string> allowedRootTypes,
        params string[] markers)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from accounts
            where company_id = @company_id
              and is_active = true
              and root_type = any(@allowed_root_types)
              and (
                system_role = any(@markers)
                or system_key = any(@markers)
              )
            order by
              case
                when system_role = any(@markers) then 0
                when system_key = any(@markers) then 1
                else 2
              end,
              code
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("allowed_root_types", NpgsqlDbType.Array | NpgsqlDbType.Text, allowedRootTypes.ToArray());
        command.Parameters.AddWithValue("markers", NpgsqlDbType.Array | NpgsqlDbType.Text, markers);

        var resolved = await command.ExecuteScalarAsync(cancellationToken);
        return resolved is Guid accountId ? accountId : null;
    }

    private sealed record InventoryAdjustmentAccountSnapshot(
        Guid Id,
        string Code,
        string Name,
        string RootType);

    private static async Task InsertInventoryAdjustmentAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        string action,
        string documentType,
        string documentNumber,
        DateOnly postingDate,
        decimal totalQuantity,
        decimal totalCostBase,
        Guid? journalEntryId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "audit_logs", cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into audit_logs (
              company_id, actor_type, actor_id, entity_type, entity_id, action, payload
            )
            values (
              @company_id, 'user', @actor_id, 'inventory_document', @entity_id, @action,
              jsonb_build_object(
                'documentType', @document_type,
                'documentNumber', @document_number,
                'postingDate', @posting_date::text,
                'totalQuantity', @total_quantity,
                'totalCostBase', @total_cost_base,
                'journalEntryId', @journal_entry_id
              )
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_id", userId.Value);
        command.Parameters.AddWithValue("entity_id", documentId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("document_number", documentNumber);
        command.Parameters.Add(new NpgsqlParameter("posting_date", NpgsqlDbType.Date) { Value = postingDate });
        command.Parameters.AddWithValue("total_quantity", Round6(totalQuantity));
        command.Parameters.AddWithValue("total_cost_base", Round6(totalCostBase));
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId.HasValue ? journalEntryId.Value.ToString("D") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsurePostingPeriodOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "company_books", cancellationToken) ||
            !await TableExistsAsync(connection, transaction, "company_book_governance_signals", cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              s.signal_date,
              s.reference_label
            from company_books b
            inner join company_book_governance_signals s
              on s.company_id = b.company_id
             and s.company_book_id = b.id
             and s.signal_type = 'closed_period'
             and s.signal_date >= @posting_date
            where b.company_id = @company_id
              and b.is_active = true
              and b.is_primary = true
              and b.effective_from <= @posting_date
            order by s.signal_date asc, s.created_at asc, s.id asc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter("posting_date", NpgsqlDbType.Date) { Value = postingDate });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var closedThrough = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("signal_date"));
        var referenceLabel = reader.IsDBNull(reader.GetOrdinal("reference_label"))
            ? "closed period"
            : reader.GetString(reader.GetOrdinal("reference_label"));
        throw new InvalidOperationException(
            $"Posting date {postingDate:yyyy-MM-dd} is locked by {referenceLabel} through {closedThrough:yyyy-MM-dd}.");
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select to_regclass(@table_name) is not null;";
        command.Parameters.AddWithValue("table_name", tableName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<Guid> ReadExistingJournalEntryIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id
            from journal_entries
            where company_id = @company_id
              and idempotency_key = @idempotency_key
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Inventory adjustment journal entry could not be resolved."));
    }

    private static async Task<string> ReserveJournalDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var seedCommand = connection.CreateCommand();
        seedCommand.Transaction = transaction;
        seedCommand.CommandText =
            """
            select coalesce(
                max(
                    case
                        when display_number ~ '^JE-[0-9]+$'
                        then substring(display_number from 4)::bigint
                        else null
                    end),
                0) + 1
            from journal_entries
            where company_id = @company_id;
            """;
        seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
        var seedNumber = Convert.ToInt64(await seedCommand.ExecuteScalarAsync(cancellationToken) ?? 1L);

        return await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            "journal-entry-display",
            "JE-",
            6,
            seedNumber,
            cancellationToken);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        return await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            $"entity-number:all:{year}",
            $"EN{year}",
            5,
            1,
            cancellationToken);
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

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

    private static string BuildPostRequestHash(InventoryAdjustmentPostRequest request)
    {
        var builder = CreateClientRequestHashBuilder("inventory-adjustment-post:v1");
        AppendCommonRequestParts(
            builder,
            request.CompanyId,
            request.UserId,
            request.AdjustmentKind.ToString(),
            request.WarehouseId,
            request.PostingDate,
            request.Memo);

        foreach (var line in request.Lines.OrderBy(line => line.LineNo))
        {
            AppendLine(builder, line);
        }

        return ComputeClientRequestHash(builder);
    }

    private static string BuildWriteOffRequestHash(InventoryWriteOffRequestPostRequest request)
    {
        var builder = CreateClientRequestHashBuilder("inventory-write-off-request:v1");
        AppendCommonRequestParts(
            builder,
            request.CompanyId,
            request.UserId,
            InventoryAdjustmentKind.WriteOff.ToString(),
            request.WarehouseId,
            request.PostingDate,
            request.Memo);

        foreach (var line in request.Lines.OrderBy(line => line.LineNo))
        {
            AppendLine(builder, line);
        }

        return ComputeClientRequestHash(builder);
    }

    private static StringBuilder CreateClientRequestHashBuilder(string version)
    {
        var builder = new StringBuilder();
        builder.Append(version).Append('\n');
        return builder;
    }

    private static void AppendCommonRequestParts(
        StringBuilder builder,
        CompanyId companyId,
        UserId userId,
        string adjustmentKind,
        Guid warehouseId,
        DateOnly postingDate,
        string? memo)
    {
        builder
            .Append("company=").Append(companyId.Value).Append('\n')
            .Append("user=").Append(userId.Value).Append('\n')
            .Append("kind=").Append(adjustmentKind).Append('\n')
            .Append("warehouse=").Append(warehouseId.ToString("D")).Append('\n')
            .Append("postingDate=").Append(postingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n')
            .Append("memo=").Append(NormalizeNullableText(memo)).Append('\n');
    }

    private static void AppendLine(StringBuilder builder, InventoryAdjustmentLineInput line)
    {
        builder
            .Append("line=").Append(line.LineNo.ToString(CultureInfo.InvariantCulture))
            .Append("|item=").Append(line.ItemId.ToString("D"))
            .Append("|uom=").Append(NormalizeText(line.UomCode).ToUpperInvariant())
            .Append("|qty=").Append(FormatDecimal(line.Quantity))
            .Append("|unitCost=").Append(line.UnitCostBase.HasValue ? FormatDecimal(line.UnitCostBase.Value) : string.Empty)
            .Append("|reason=").Append(NormalizeNullableText(line.ReasonCode))
            .Append("|memo=").Append(NormalizeNullableText(line.Memo))
            .Append('\n');
    }

    private static string ComputeClientRequestHash(StringBuilder builder)
    {
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string FormatDecimal(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);

    private static string NormalizeNullableText(string? value) =>
        value is null ? string.Empty : NormalizeText(value);

    private static string NormalizeText(string value) =>
        value.Trim();

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
            if (!await CoreSchemaExistsAsync(connection, cancellationToken))
            {
                throw new InvalidOperationException(
                    "Inventory adjustment schema has not been installed. Apply database migrations before using inventory adjustments.");
            }

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
                  and column_name = 'document_number')
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
                  and column_name = 'posted_by_user_id')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'client_request_hash');
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

    private sealed record InventoryAdjustmentJournalCandidate(
        int SourceLineNumber,
        string ItemCode,
        Guid InventoryAssetAccountId,
        Guid AdjustmentAccountId,
        decimal AmountBase);

    private sealed record InventoryAdjustmentJournalLine(
        Guid AccountId,
        string Description,
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit,
        string PostingRole,
        int SourceLineNumber);

    private static class InventoryAdjustmentJournalPlan
    {
        public static IReadOnlyList<InventoryAdjustmentJournalLine> Build(
            InventoryAdjustmentKind adjustmentKind,
            string documentNumber,
            IReadOnlyList<InventoryAdjustmentJournalCandidate> candidates)
        {
            var lines = new List<InventoryAdjustmentJournalLine>();
            foreach (var candidate in candidates.OrderBy(static candidate => candidate.SourceLineNumber))
            {
                var amount = Round6(candidate.AmountBase);
                if (amount <= 0m)
                {
                    continue;
                }

                var itemCode = string.IsNullOrWhiteSpace(candidate.ItemCode)
                    ? "item"
                    : candidate.ItemCode.Trim();
                var description = $"{documentNumber} line {candidate.SourceLineNumber}: {itemCode}";

                switch (adjustmentKind)
                {
                    case InventoryAdjustmentKind.Gain:
                        lines.Add(new InventoryAdjustmentJournalLine(
                            candidate.InventoryAssetAccountId,
                            description,
                            amount,
                            0m,
                            amount,
                            0m,
                            "inventory_asset",
                            candidate.SourceLineNumber));
                        lines.Add(new InventoryAdjustmentJournalLine(
                            candidate.AdjustmentAccountId,
                            description,
                            0m,
                            amount,
                            0m,
                            amount,
                            "inventory_adjustment",
                            candidate.SourceLineNumber));
                        break;

                    case InventoryAdjustmentKind.Loss:
                    case InventoryAdjustmentKind.WriteOff:
                        lines.Add(new InventoryAdjustmentJournalLine(
                            candidate.AdjustmentAccountId,
                            description,
                            amount,
                            0m,
                            amount,
                            0m,
                            "inventory_adjustment",
                            candidate.SourceLineNumber));
                        lines.Add(new InventoryAdjustmentJournalLine(
                            candidate.InventoryAssetAccountId,
                            description,
                            0m,
                            amount,
                            0m,
                            amount,
                            "inventory_asset",
                            candidate.SourceLineNumber));
                        break;

                    default:
                        throw new InvalidOperationException($"Unsupported inventory adjustment kind '{adjustmentKind}'.");
                }
            }

            return lines;
        }
    }
}
