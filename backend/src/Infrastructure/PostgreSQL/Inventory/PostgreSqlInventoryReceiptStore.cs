using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryReceiptStore : IInventoryReceiptStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryReceiptStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<InventoryPurchaseReceiptDashboard> GetDashboardAsync(
        Guid companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, null, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, null, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, null, companyId, cancellationToken);
        var recentReceipts = await LoadRecentReceiptsAsync(connection, null, companyId, cancellationToken);

        return new InventoryPurchaseReceiptDashboard(
            companyId,
            baseCurrencyCode,
            activeItems,
            activeWarehouses,
            recentReceipts);
    }

    public async Task<InventoryBillReceiptHandoffSummary> GetBillHandoffSummaryAsync(
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var (billInboundLineCount, billInboundQuantity) = await LoadBillInboundSummaryAsync(
            connection,
            null,
            companyId,
            billDocumentId,
            cancellationToken);
        var lineSummaries = await LoadBillHandoffLineSummariesAsync(
            connection,
            null,
            companyId,
            billDocumentId,
            cancellationToken);
        var recentReceipts = await LoadBillAnchoredReceiptsAsync(
            connection,
            null,
            companyId,
            billDocumentId,
            cancellationToken);
        var receivedQuantity = decimal.Round(
            recentReceipts.Sum(static receipt => receipt.TotalQuantity),
            6,
            MidpointRounding.AwayFromZero);
        var remainingQuantity = decimal.Round(
            billInboundQuantity - receivedQuantity,
            6,
            MidpointRounding.AwayFromZero);
        var matchStatus =
            billInboundLineCount == 0
                ? "no_inventory_handoff"
                : recentReceipts.Count == 0
                    ? "no_receipt"
                    : remainingQuantity > 0m
                        ? "partially_receipted"
                        : remainingQuantity == 0m
                            ? "fully_receipted"
                            : "over_receipted";

        return new InventoryBillReceiptHandoffSummary(
            billDocumentId,
            billInboundLineCount,
            billInboundQuantity,
            recentReceipts.Count,
            receivedQuantity,
            remainingQuantity,
            matchStatus,
            recentReceipts.FirstOrDefault()?.PostedAt,
            recentReceipts,
            lineSummaries);
    }

    public async Task<LegacyInboundReceiptPathSnapshot?> GetLegacyInboundReceiptPathSnapshotAsync(
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        var handoff = await GetBillHandoffSummaryAsync(companyId, billDocumentId, cancellationToken);
        var firstClassCoverage = await LoadFirstClassReceiptCoverageAsync(
            companyId,
            billDocumentId,
            cancellationToken);
        var coverageByAnchor = firstClassCoverage.ToDictionary(
            static row => new LegacyInboundReceiptCoverageKey(row.ItemId, row.WarehouseId, row.UomCode),
            static row => row);

        var lines = handoff.LineSummaries
            .Select(line =>
            {
                var key = new LegacyInboundReceiptCoverageKey(
                    line.ItemId,
                    line.WarehouseId,
                    line.UomCode.Trim().ToUpperInvariant());
                coverageByAnchor.TryGetValue(key, out var coverage);

                return new LegacyInboundReceiptPathLineSnapshot(
                    line.ItemId,
                    line.ItemCode,
                    line.WarehouseId,
                    line.WarehouseCode,
                    line.UomCode,
                    line.BillQuantity,
                    line.ReceivedQuantity,
                    coverage?.CoveredQuantity ?? 0m);
            })
            .ToArray();

        return new LegacyInboundReceiptPathSnapshot(
            billDocumentId,
            handoff.BillInboundLineCount,
            handoff.BillInboundQuantity,
            handoff.ReceiptCount,
            handoff.ReceivedQuantity,
            firstClassCoverage.Sum(static row => row.AllocationCount),
            decimal.Round(firstClassCoverage.Sum(static row => row.CoveredQuantity), 6, MidpointRounding.AwayFromZero),
            lines);
    }

    public async Task<IReadOnlyDictionary<Guid, InventoryBillReceiptPostingGateSnapshot>> GetBillPostingGateSnapshotsAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> billDocumentIds,
        CancellationToken cancellationToken)
    {
        if (billDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, InventoryBillReceiptPostingGateSnapshot>();
        }

        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with requested_bills as (
              select unnest(@bill_document_ids::uuid[]) as bill_document_id
            ),
            bill_groups as (
              select
                l.bill_id as bill_document_id,
                count(*)::int as bill_inbound_line_count,
                coalesce(sum(l.quantity), 0) as bill_inbound_quantity
              from bill_lines l
              where l.company_id = @company_id
                and l.bill_id = any(@bill_document_ids)
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
                and l.quantity is not null
                and l.unit_cost is not null
              group by l.bill_id
            ),
            receipt_groups as (
              select
                d.source_document_id as bill_document_id,
                count(distinct d.id)::int as receipt_count,
                coalesce(sum(l.base_quantity), 0) as received_quantity,
                max(d.posted_at) as latest_receipt_posted_at
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'purchase_receipt'
                and d.source_module = 'ap_bill'
                and d.source_document_id = any(@bill_document_ids)
              group by d.source_document_id
            )
            select
              rb.bill_document_id,
              coalesce(bg.bill_inbound_line_count, 0) as bill_inbound_line_count,
              coalesce(bg.bill_inbound_quantity, 0) as bill_inbound_quantity,
              coalesce(rg.receipt_count, 0) as receipt_count,
              coalesce(rg.received_quantity, 0) as received_quantity,
              rg.latest_receipt_posted_at
            from requested_bills rb
            left join bill_groups bg
              on bg.bill_document_id = rb.bill_document_id
            left join receipt_groups rg
              on rg.bill_document_id = rb.bill_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("bill_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = billDocumentIds.Distinct().ToArray()
        });
        command.Parameters.AddWithValue("company_id", companyId);

        var snapshots = new Dictionary<Guid, InventoryBillReceiptPostingGateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var billDocumentId = reader.GetGuid(reader.GetOrdinal("bill_document_id"));
            var billInboundLineCount = reader.GetInt32(reader.GetOrdinal("bill_inbound_line_count"));
            var billInboundQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_inbound_quantity")), 6, MidpointRounding.AwayFromZero);
            var receiptCount = reader.GetInt32(reader.GetOrdinal("receipt_count"));
            var receivedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("received_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(billInboundQuantity - receivedQuantity, 6, MidpointRounding.AwayFromZero);
            var matchStatus =
                billInboundLineCount == 0
                    ? "no_inventory_handoff"
                    : receiptCount == 0
                        ? "no_receipt"
                        : remainingQuantity > 0m
                            ? "partially_receipted"
                            : remainingQuantity == 0m
                                ? "fully_receipted"
                                : "over_receipted";

            snapshots[billDocumentId] = new InventoryBillReceiptPostingGateSnapshot(
                billDocumentId,
                billInboundLineCount,
                billInboundQuantity,
                receiptCount,
                receivedQuantity,
                remainingQuantity,
                matchStatus,
                reader.IsDBNull(reader.GetOrdinal("latest_receipt_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_receipt_posted_at")));
        }

        return snapshots;
    }

    public async Task<InventoryPurchaseReceiptSummary> PostAsync(
        InventoryPurchaseReceiptPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, transaction, request.CompanyId, cancellationToken);
            var normalizedTransactionCurrencyCode = request.TransactionCurrencyCode.Trim().ToUpperInvariant();
            var normalizedBaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant();
            var normalizedFxRate =
                normalizedTransactionCurrencyCode == normalizedBaseCurrencyCode
                    ? 1m
                    : request.FxRateToBase;

            if (normalizedFxRate <= 0)
            {
                throw new InvalidOperationException("FX rate to base must be greater than zero.");
            }

            var vendorDisplayName = await LoadVendorDisplayNameAsync(
                connection,
                transaction,
                request.CompanyId,
                request.VendorId,
                cancellationToken);

            var itemMap = await LoadItemMapAsync(
                connection,
                transaction,
                request.CompanyId,
                request.Lines.Select(line => line.ItemId).Distinct().ToArray(),
                cancellationToken);
            var warehouseMap = await LoadWarehouseMapAsync(
                connection,
                transaction,
                request.CompanyId,
                request.Lines.Select(line => line.WarehouseId).Distinct().ToArray(),
                cancellationToken);

            foreach (var line in request.Lines)
            {
                if (!itemMap.TryGetValue(line.ItemId, out var item))
                {
                    throw new InvalidOperationException("Each purchase receipt line must reference an active inventory item in this company.");
                }

                if (item.ItemKind != InventoryItemKind.Stock)
                {
                    throw new InvalidOperationException($"Inventory receipt only supports stock items. '{item.Name}' is not a stock item.");
                }

                if (item.ManageInventoryMethod == ManageInventoryMethod.DontManageStock)
                {
                    throw new InvalidOperationException($"Inventory receipt requires stock-managed items. '{item.Name}' is configured as non-stock-managed.");
                }

                if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Inventory receipt line UOM must match the stock UOM for '{item.Name}'.");
                }

                if (!warehouseMap.ContainsKey(line.WarehouseId))
                {
                    throw new InvalidOperationException("Each purchase receipt line must reference an active warehouse in this company.");
                }
            }

            var documentId = Guid.NewGuid();
            var documentNumber = BuildReceiptNumber(request.PostingDate);
            var createdAt = DateTimeOffset.UtcNow;
            var totalQuantity = 0m;
            var totalCostBase = 0m;

            await using (var insertDocumentCommand = connection.CreateCommand())
            {
                insertDocumentCommand.Transaction = transaction;
                insertDocumentCommand.CommandText =
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
                      'purchase_receipt',
                      'posted',
                      'inbound',
                      @posting_date,
                      @source_module,
                      @source_document_id,
                      @source_document_number,
                      @counterparty_id,
                      @memo,
                      @created_by_user_id,
                      @created_at,
                      @posted_at
                    );
                    """;
                insertDocumentCommand.Parameters.AddWithValue("id", documentId);
                insertDocumentCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                insertDocumentCommand.Parameters.AddWithValue("document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_module", ToDbValue(request.SourceModule));
                insertDocumentCommand.Parameters.AddWithValue("source_document_id", request.SourceDocumentId ?? (object)DBNull.Value);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", ToDbValue(request.SourceDocumentNumber));
                insertDocumentCommand.Parameters.AddWithValue("counterparty_id", request.VendorId);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", request.UserId);
                insertDocumentCommand.Parameters.AddWithValue("created_at", createdAt);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", createdAt);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                var lineId = Guid.NewGuid();
                var normalizedUomCode = line.UomCode.Trim().ToUpperInvariant();
                var baseQuantity = decimal.Round(line.Quantity, 6, MidpointRounding.AwayFromZero);
                var unitCostBase = decimal.Round(line.UnitCostTx * normalizedFxRate, 6, MidpointRounding.AwayFromZero);
                var extendedCostBase = decimal.Round(baseQuantity * unitCostBase, 6, MidpointRounding.AwayFromZero);
                var currentOnHand = await LoadCurrentOnHandAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    cancellationToken);
                var quantityAfter = decimal.Round(currentOnHand + baseQuantity, 6, MidpointRounding.AwayFromZero);
                var currentCostBalance = await LoadCurrentCostBalanceAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    cancellationToken);
                var costAfter = decimal.Round(currentCostBalance + extendedCostBase, 6, MidpointRounding.AwayFromZero);
                var ledgerEntryId = Guid.NewGuid();
                var costLayerId = Guid.NewGuid();

                await using (var insertLineCommand = connection.CreateCommand())
                {
                    insertLineCommand.Transaction = transaction;
                    insertLineCommand.CommandText =
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
                    insertLineCommand.Parameters.AddWithValue("id", lineId);
                    insertLineCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                    insertLineCommand.Parameters.AddWithValue("document_id", documentId);
                    insertLineCommand.Parameters.AddWithValue("line_no", line.LineNo);
                    insertLineCommand.Parameters.AddWithValue("item_id", line.ItemId);
                    insertLineCommand.Parameters.AddWithValue("warehouse_id", line.WarehouseId);
                    insertLineCommand.Parameters.AddWithValue("uom_code", normalizedUomCode);
                    insertLineCommand.Parameters.AddWithValue("quantity", line.Quantity);
                    insertLineCommand.Parameters.AddWithValue("base_quantity", baseQuantity);
                    insertLineCommand.Parameters.AddWithValue("currency_code", normalizedTransactionCurrencyCode);
                    insertLineCommand.Parameters.AddWithValue("fx_rate_to_base", normalizedFxRate);
                    insertLineCommand.Parameters.AddWithValue("unit_cost_tx", line.UnitCostTx);
                    insertLineCommand.Parameters.AddWithValue("unit_cost_base", unitCostBase);
                    insertLineCommand.Parameters.AddWithValue("extended_cost_base", extendedCostBase);
                    insertLineCommand.Parameters.AddWithValue("reason_code", ToDbValue(line.ReasonCode));
                    insertLineCommand.Parameters.AddWithValue("memo", ToDbValue(line.Memo));
                    await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await UpsertBalanceAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    baseQuantity,
                    cancellationToken);

                await using (var insertLedgerCommand = connection.CreateCommand())
                {
                    insertLedgerCommand.Transaction = transaction;
                    insertLedgerCommand.CommandText =
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
                          'inbound',
                          'purchase_receipt',
                          @posting_date,
                          @quantity_delta,
                          @quantity_after,
                          @cost_amount_delta_base,
                          @cost_amount_after_base,
                          @memo,
                          @created_at
                        );
                        """;
                    insertLedgerCommand.Parameters.AddWithValue("id", ledgerEntryId);
                    insertLedgerCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                    insertLedgerCommand.Parameters.AddWithValue("item_id", line.ItemId);
                    insertLedgerCommand.Parameters.AddWithValue("warehouse_id", line.WarehouseId);
                    insertLedgerCommand.Parameters.AddWithValue("document_id", documentId);
                    insertLedgerCommand.Parameters.AddWithValue("document_line_id", lineId);
                    insertLedgerCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                    insertLedgerCommand.Parameters.AddWithValue("quantity_delta", baseQuantity);
                    insertLedgerCommand.Parameters.AddWithValue("quantity_after", quantityAfter);
                    insertLedgerCommand.Parameters.AddWithValue("cost_amount_delta_base", extendedCostBase);
                    insertLedgerCommand.Parameters.AddWithValue("cost_amount_after_base", costAfter);
                    insertLedgerCommand.Parameters.AddWithValue("memo", BuildLedgerMemo(documentNumber, line.LineNo));
                    insertLedgerCommand.Parameters.AddWithValue("created_at", createdAt);
                    await insertLedgerCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var insertCostLayerCommand = connection.CreateCommand())
                {
                    insertCostLayerCommand.Transaction = transaction;
                    insertCostLayerCommand.CommandText =
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
                    insertCostLayerCommand.Parameters.AddWithValue("id", costLayerId);
                    insertCostLayerCommand.Parameters.AddWithValue("company_id", request.CompanyId);
                    insertCostLayerCommand.Parameters.AddWithValue("item_id", line.ItemId);
                    insertCostLayerCommand.Parameters.AddWithValue("warehouse_id", line.WarehouseId);
                    insertCostLayerCommand.Parameters.AddWithValue("source_document_id", documentId);
                    insertCostLayerCommand.Parameters.AddWithValue("source_document_line_id", lineId);
                    insertCostLayerCommand.Parameters.AddWithValue("source_ledger_entry_id", ledgerEntryId);
                    insertCostLayerCommand.Parameters.AddWithValue("layer_date", request.PostingDate);
                    insertCostLayerCommand.Parameters.AddWithValue("original_qty", baseQuantity);
                    insertCostLayerCommand.Parameters.AddWithValue("remaining_qty", baseQuantity);
                    insertCostLayerCommand.Parameters.AddWithValue("unit_cost_base", unitCostBase);
                    insertCostLayerCommand.Parameters.AddWithValue("remaining_cost_base", extendedCostBase);
                    insertCostLayerCommand.Parameters.AddWithValue("created_at", createdAt);
                    await insertCostLayerCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                totalQuantity += baseQuantity;
                totalCostBase += extendedCostBase;
            }

            var summary = new InventoryPurchaseReceiptSummary(
                documentId,
                request.CompanyId,
                documentNumber,
                "posted",
                request.PostingDate,
                request.VendorId,
                vendorDisplayName,
                normalizedTransactionCurrencyCode,
                normalizedBaseCurrencyCode,
                normalizedFxRate,
                decimal.Round(totalQuantity, 6, MidpointRounding.AwayFromZero),
                decimal.Round(totalCostBase, 6, MidpointRounding.AwayFromZero),
                request.Lines.Count,
                createdAt,
                createdAt,
                NormalizeOptionalText(request.Memo));

            await transaction.CommitAsync(cancellationToken);
            return summary;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Another inventory receipt already uses the same company-scoped receipt number.", ex);
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

    private static async Task<string> LoadCompanyBaseCurrencyCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select base_currency_code
            from companies
            where id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string baseCurrencyCode || string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            throw new InvalidOperationException("Company base currency could not be found.");
        }

        return baseCurrencyCode.Trim().ToUpperInvariant();
    }

    private static async Task<string> LoadVendorDisplayNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select display_name
            from vendors
            where id = @vendor_id
              and company_id = @company_id
              and is_active = true
            limit 1;
            """;
        command.Parameters.AddWithValue("vendor_id", vendorId);
        command.Parameters.AddWithValue("company_id", companyId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string displayName || string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("The selected vendor could not be found for this company or is inactive.");
        }

        return displayName.Trim();
    }

    private static async Task<Dictionary<Guid, InventoryManagedItemSummary>> LoadItemMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
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
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_ids", itemIds.ToArray());

        var items = new Dictionary<Guid, InventoryManagedItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = new InventoryManagedItemSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
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
            items[summary.Id] = summary;
        }

        return items;
    }

    private static async Task<Dictionary<Guid, InventoryManagedWarehouseSummary>> LoadWarehouseMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
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
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("warehouse_ids", warehouseIds.ToArray());

        var warehouses = new Dictionary<Guid, InventoryManagedWarehouseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = new InventoryManagedWarehouseSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
            warehouses[summary.Id] = summary;
        }

        return warehouses;
    }

    private static async Task<decimal> LoadCurrentOnHandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid itemId,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(on_hand_qty, 0)
            from item_warehouse_balances
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
    }

    private static async Task<decimal> LoadCurrentCostBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid itemId,
        Guid warehouseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(sum(cost_amount_delta_base), 0)
            from inventory_ledger_entries
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
    }

    private static async Task UpsertBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
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

    private static async Task<IReadOnlyList<InventoryManagedItemSummary>> LoadActiveItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
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
              and manage_inventory_method <> 'dont_manage_stock'
            order by item_code asc, name asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var items = new List<InventoryManagedItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new InventoryManagedItemSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
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
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return items;
    }

    private static async Task<IReadOnlyList<InventoryManagedWarehouseSummary>> LoadActiveWarehousesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
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
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return warehouses;
    }

    private static async Task<IReadOnlyList<InventoryPurchaseReceiptSummary>> LoadRecentReceiptsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              d.id,
              d.company_id,
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              d.status,
              d.posting_date,
              d.counterparty_id as vendor_id,
              coalesce(v.display_name, 'Unknown vendor') as vendor_display_name,
              coalesce(max(l.currency_code), c.base_currency_code) as transaction_currency_code,
              c.base_currency_code,
              coalesce(max(l.fx_rate_to_base), 1) as fx_rate_to_base,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              coalesce(sum(l.extended_cost_base), 0) as total_cost_base,
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.memo
            from inventory_documents d
            join companies c
              on c.id = d.company_id
            left join vendors v
              on v.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
            where d.company_id = @company_id
              and d.document_type = 'purchase_receipt'
            group by
              d.id,
              d.company_id,
              d.document_number,
              d.source_document_number,
              d.status,
              d.posting_date,
              d.counterparty_id,
              v.display_name,
              c.base_currency_code,
              d.created_at,
              d.posted_at,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId);

        var receipts = new List<InventoryPurchaseReceiptSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new InventoryPurchaseReceiptSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetString(reader.GetOrdinal("vendor_display_name")),
                reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("fx_rate_to_base")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_cost_base")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return receipts;
    }

    private async Task<IReadOnlyList<LegacyInboundReceiptCoverageRow>> LoadFirstClassReceiptCoverageAsync(
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        if (!await TableExistsAsync(connection, "receipts", cancellationToken) ||
            !await TableExistsAsync(connection, "receipt_lines", cancellationToken))
        {
            return Array.Empty<LegacyInboundReceiptCoverageRow>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with bill_groups as (
              select
                b.vendor_id,
                l.item_id,
                l.warehouse_id,
                upper(trim(l.uom_code)) as uom_code
              from bills b
              join bill_lines l
                on l.company_id = b.company_id
               and l.bill_id = b.id
              where b.company_id = @company_id
                and b.id = @bill_document_id
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
                and l.quantity is not null
                and l.unit_cost is not null
              group by
                b.vendor_id,
                l.item_id,
                l.warehouse_id,
                upper(trim(l.uom_code))
            )
            select
              bg.item_id,
              bg.warehouse_id,
              bg.uom_code,
              count(rl.id)::int as coverage_count,
              coalesce(sum(rl.quantity), 0) as covered_quantity
            from bill_groups bg
            join receipts r
              on r.company_id = @company_id
             and r.vendor_id = bg.vendor_id
             and r.warehouse_id = bg.warehouse_id
             and r.status = 'posted'
            join receipt_lines rl
              on rl.company_id = r.company_id
             and rl.receipt_id = r.id
             and rl.item_id = bg.item_id
             and upper(trim(rl.uom_code)) = bg.uom_code
            group by
              bg.item_id,
              bg.warehouse_id,
              bg.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        var rows = new List<LegacyInboundReceiptCoverageRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyInboundReceiptCoverageRow(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetInt32(reader.GetOrdinal("coverage_count")),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("covered_quantity")), 6, MidpointRounding.AwayFromZero)));
        }

        return rows;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select to_regclass(@table_name) is not null;";
        command.Parameters.AddWithValue("table_name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static async Task<(int LineCount, decimal Quantity)> LoadBillInboundSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              count(*)::int as line_count,
              coalesce(sum(l.quantity), 0) as total_quantity
            from bill_lines l
            join bills b
              on b.id = l.bill_id
             and b.company_id = l.company_id
            where l.company_id = @company_id
              and l.bill_id = @bill_document_id
              and l.item_id is not null
              and l.warehouse_id is not null
              and l.uom_code is not null
              and l.quantity is not null
              and l.unit_cost is not null;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0m);
        }

        return (
            reader.GetInt32(reader.GetOrdinal("line_count")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")));
    }

    private static async Task<IReadOnlyList<InventoryBillReceiptHandoffLineSummary>> LoadBillHandoffLineSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with bill_groups as (
                select
                  l.item_id,
                  i.item_code,
                  i.name as item_name,
                  l.warehouse_id,
                  w.warehouse_code,
                  w.name as warehouse_name,
                  upper(l.uom_code) as uom_code,
                  count(*)::int as bill_line_count,
                  coalesce(sum(l.quantity), 0) as bill_quantity
                from bill_lines l
                join bills b
                  on b.id = l.bill_id
                 and b.company_id = l.company_id
                join inventory_items i
                  on i.id = l.item_id
                 and i.company_id = l.company_id
                join inventory_warehouses w
                  on w.id = l.warehouse_id
                 and w.company_id = l.company_id
                where l.company_id = @company_id
                  and l.bill_id = @bill_document_id
                  and l.item_id is not null
                  and l.warehouse_id is not null
                  and l.uom_code is not null
                  and l.quantity is not null
                  and l.unit_cost is not null
                group by
                  l.item_id,
                  i.item_code,
                  i.name,
                  l.warehouse_id,
                  w.warehouse_code,
                  w.name,
                  upper(l.uom_code)
            ),
            receipt_groups as (
                select
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code) as uom_code,
                  coalesce(sum(l.base_quantity), 0) as received_quantity
                from inventory_documents d
                join inventory_document_lines l
                  on l.document_id = d.id
                 and l.company_id = d.company_id
                where d.company_id = @company_id
                  and d.document_type = 'purchase_receipt'
                  and d.source_module = 'ap_bill'
                  and d.source_document_id = @bill_document_id
                group by
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code)
            )
            select
              b.item_id,
              b.item_code,
              b.item_name,
              b.warehouse_id,
              b.warehouse_code,
              b.warehouse_name,
              b.uom_code,
              b.bill_line_count,
              b.bill_quantity,
              coalesce(r.received_quantity, 0) as received_quantity
            from bill_groups b
            left join receipt_groups r
              on r.item_id = b.item_id
             and r.warehouse_id = b.warehouse_id
             and r.uom_code = b.uom_code
            order by b.item_code, b.warehouse_code, b.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        var rows = new List<InventoryBillReceiptHandoffLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var billQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("bill_quantity")), 6, MidpointRounding.AwayFromZero);
            var receivedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("received_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(billQuantity - receivedQuantity, 6, MidpointRounding.AwayFromZero);
            var matchStatus =
                receivedQuantity <= 0m
                    ? "no_receipt"
                    : remainingQuantity > 0m
                        ? "partially_receipted"
                        : remainingQuantity == 0m
                            ? "fully_receipted"
                            : "over_receipted";

            rows.Add(new InventoryBillReceiptHandoffLineSummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetInt32(reader.GetOrdinal("bill_line_count")),
                billQuantity,
                receivedQuantity,
                remainingQuantity,
                matchStatus));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<InventoryPurchaseReceiptSummary>> LoadBillAnchoredReceiptsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              d.id,
              d.company_id,
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              d.status,
              d.posting_date,
              d.counterparty_id as vendor_id,
              coalesce(v.display_name, 'Unknown vendor') as vendor_display_name,
              coalesce(max(l.currency_code), c.base_currency_code) as transaction_currency_code,
              c.base_currency_code,
              coalesce(max(l.fx_rate_to_base), 1) as fx_rate_to_base,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              coalesce(sum(l.extended_cost_base), 0) as total_cost_base,
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.memo
            from inventory_documents d
            join companies c
              on c.id = d.company_id
            left join vendors v
              on v.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
            where d.company_id = @company_id
              and d.document_type = 'purchase_receipt'
              and d.source_module = 'ap_bill'
              and d.source_document_id = @bill_document_id
            group by
              d.id,
              d.company_id,
              d.document_number,
              d.source_document_number,
              d.status,
              d.posting_date,
              d.counterparty_id,
              v.display_name,
              c.base_currency_code,
              d.created_at,
              d.posted_at,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 5;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("bill_document_id", billDocumentId);

        var receipts = new List<InventoryPurchaseReceiptSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new InventoryPurchaseReceiptSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("company_id")),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetString(reader.GetOrdinal("vendor_display_name")),
                reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("fx_rate_to_base")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")),
                reader.GetFieldValue<decimal>(reader.GetOrdinal("total_cost_base")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return receipts;
    }

    private readonly record struct LegacyInboundReceiptCoverageKey(
        Guid ItemId,
        Guid WarehouseId,
        string UomCode);

    private sealed record LegacyInboundReceiptCoverageRow(
        Guid ItemId,
        Guid WarehouseId,
        string UomCode,
        int AllocationCount,
        decimal CoveredQuantity);

    private static string BuildReceiptNumber(DateOnly postingDate) =>
        $"PR-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string BuildLedgerMemo(string documentNumber, int lineNo) =>
        $"{documentNumber} line {lineNo}";

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
