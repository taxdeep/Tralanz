using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryIssueStore : IInventoryIssueStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryIssueStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<InventorySalesIssueDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, null, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, null, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, null, companyId, cancellationToken);
        var recentIssues = await LoadRecentIssuesAsync(connection, null, companyId, cancellationToken);

        return new InventorySalesIssueDashboard(
            companyId,
            baseCurrencyCode,
            activeItems,
            activeWarehouses,
            recentIssues);
    }

    public async Task<InventoryInvoiceIssueHandoffSummary> GetInvoiceHandoffSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var (invoiceOutboundLineCount, invoiceOutboundQuantity) = await LoadInvoiceOutboundSummaryAsync(
            connection,
            null,
            companyId,
            invoiceDocumentId,
            cancellationToken);
        var lineSummaries = await LoadInvoiceHandoffLineSummariesAsync(
            connection,
            null,
            companyId,
            invoiceDocumentId,
            cancellationToken);
        var recentIssues = await LoadInvoiceAnchoredIssuesAsync(
            connection,
            null,
            companyId,
            invoiceDocumentId,
            cancellationToken);
        var issuedQuantity = decimal.Round(
            recentIssues.Sum(static issue => issue.TotalQuantity),
            6,
            MidpointRounding.AwayFromZero);
        var remainingQuantity = decimal.Round(
            invoiceOutboundQuantity - issuedQuantity,
            6,
            MidpointRounding.AwayFromZero);
        var matchStatus =
            invoiceOutboundLineCount == 0
                ? "no_inventory_handoff"
                : recentIssues.Count == 0
                    ? "no_issue"
                    : remainingQuantity > 0m
                        ? "partially_issued"
                        : remainingQuantity == 0m
                            ? "fully_issued"
                            : "over_issued";

        return new InventoryInvoiceIssueHandoffSummary(
            invoiceDocumentId,
            invoiceOutboundLineCount,
            invoiceOutboundQuantity,
            recentIssues.Count,
            issuedQuantity,
            remainingQuantity,
            matchStatus,
            recentIssues.FirstOrDefault()?.PostedAt,
            recentIssues,
            lineSummaries);
    }

    public async Task<IReadOnlyDictionary<Guid, InventoryInvoiceIssuePostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken)
    {
        if (invoiceDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, InventoryInvoiceIssuePostingGateSnapshot>();
        }

        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with requested_invoices as (
              select unnest(@invoice_document_ids::uuid[]) as invoice_document_id
            ),
            invoice_groups as (
              select
                l.invoice_id as invoice_document_id,
                count(*)::int as invoice_outbound_line_count,
                coalesce(sum(l.quantity), 0) as invoice_outbound_quantity
              from invoice_lines l
              where l.company_id = @company_id
                and l.invoice_id = any(@invoice_document_ids)
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
              group by l.invoice_id
            ),
            issue_groups as (
              select
                d.source_document_id as invoice_document_id,
                count(distinct d.id)::int as issue_count,
                coalesce(sum(l.base_quantity), 0) as issued_quantity,
                max(d.posted_at) as latest_issue_posted_at
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'sales_issue'
                and d.source_module = 'ar_invoice'
                and d.source_document_id = any(@invoice_document_ids)
              group by d.source_document_id
            )
            select
              ri.invoice_document_id,
              coalesce(ig.invoice_outbound_line_count, 0) as invoice_outbound_line_count,
              coalesce(ig.invoice_outbound_quantity, 0) as invoice_outbound_quantity,
              coalesce(sg.issue_count, 0) as issue_count,
              coalesce(sg.issued_quantity, 0) as issued_quantity,
              sg.latest_issue_posted_at
            from requested_invoices ri
            left join invoice_groups ig
              on ig.invoice_document_id = ri.invoice_document_id
            left join issue_groups sg
              on sg.invoice_document_id = ri.invoice_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("invoice_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = invoiceDocumentIds.Distinct().ToArray()
        });
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var snapshots = new Dictionary<Guid, InventoryInvoiceIssuePostingGateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceDocumentId = reader.GetGuid(reader.GetOrdinal("invoice_document_id"));
            var invoiceOutboundLineCount = reader.GetInt32(reader.GetOrdinal("invoice_outbound_line_count"));
            var invoiceOutboundQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("invoice_outbound_quantity")), 6, MidpointRounding.AwayFromZero);
            var issueCount = reader.GetInt32(reader.GetOrdinal("issue_count"));
            var issuedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("issued_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(invoiceOutboundQuantity - issuedQuantity, 6, MidpointRounding.AwayFromZero);
            var matchStatus =
                invoiceOutboundLineCount == 0
                    ? "no_inventory_handoff"
                    : issueCount == 0
                        ? "no_issue"
                        : remainingQuantity > 0m
                            ? "partially_issued"
                            : remainingQuantity == 0m
                                ? "fully_issued"
                                : "over_issued";

            snapshots[invoiceDocumentId] = new InventoryInvoiceIssuePostingGateSnapshot(
                invoiceDocumentId,
                invoiceOutboundLineCount,
                invoiceOutboundQuantity,
                issueCount,
                issuedQuantity,
                remainingQuantity,
                matchStatus,
                reader.IsDBNull(reader.GetOrdinal("latest_issue_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_issue_posted_at")));
        }

        return snapshots;
    }

    public async Task<InventorySalesIssueSummary> PostAsync(
        InventorySalesIssuePostRequest request,
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
            var customerDisplayName = await LoadCustomerDisplayNameAsync(
                connection,
                transaction,
                request.CompanyId,
                request.CustomerId,
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
                    throw new InvalidOperationException("Each sales issue line must reference an active inventory item in this company.");
                }

                if (item.ItemKind != InventoryItemKind.Stock)
                {
                    throw new InvalidOperationException($"Sales issue only supports stock items. '{item.Name}' is not a stock item.");
                }

                if (item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
                {
                    throw new InvalidOperationException($"Sales issue currently supports only warehouse-managed stock items. '{item.Name}' is not configured on that path.");
                }

                if (!warehouseMap.ContainsKey(line.WarehouseId))
                {
                    throw new InvalidOperationException("Each sales issue line must reference an active warehouse in this company.");
                }

                if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Sales issue line UOM must match the stock UOM for '{item.Name}'.");
                }
            }

            var documentId = Guid.NewGuid();
            var documentNumber = BuildIssueNumber(request.PostingDate);
            var createdAt = DateTimeOffset.UtcNow;
            var totalQuantity = 0m;
            var totalCostBase = 0m;
            var negativeStockAllowed = foundationSummary.CostingPolicy?.NegativeStockAllowed == true;

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
                      'sales_issue',
                      'posted',
                      'outbound',
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
                insertDocumentCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                insertDocumentCommand.Parameters.AddWithValue("document_number", documentNumber);
                insertDocumentCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                insertDocumentCommand.Parameters.AddWithValue("source_module", ToDbValue(request.SourceModule));
                insertDocumentCommand.Parameters.AddWithValue("source_document_id", request.SourceDocumentId ?? (object)DBNull.Value);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", ToDbValue(request.SourceDocumentNumber));
                insertDocumentCommand.Parameters.AddWithValue("counterparty_id", request.CustomerId);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", request.UserId.Value);
                insertDocumentCommand.Parameters.AddWithValue("created_at", createdAt);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", createdAt);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                var item = itemMap[line.ItemId];
                var lineId = Guid.NewGuid();
                var normalizedUomCode = line.UomCode.Trim().ToUpperInvariant();
                var baseQuantity = decimal.Round(line.Quantity, 6, MidpointRounding.AwayFromZero);
                var balance = await LoadCurrentBalanceAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    cancellationToken);
                var availableQuantity = decimal.Round(balance.OnHandQty - balance.ReservedQty, 6, MidpointRounding.AwayFromZero);

                if (!negativeStockAllowed && availableQuantity < baseQuantity)
                {
                    throw new InvalidOperationException($"Sales issue cannot post because '{item.Name}' only has {availableQuantity} available in the selected warehouse.");
                }

                if (negativeStockAllowed &&
                    availableQuantity < baseQuantity &&
                    item.BackorderMode == InventoryBackorderMode.Disallow)
                {
                    throw new InvalidOperationException($"Sales issue cannot push '{item.Name}' negative because the item backorder mode is set to disallow.");
                }

                var costLayers = await LoadOpenCostLayersAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    cancellationToken);
                var openCostLayerQuantity = decimal.Round(
                    costLayers.Sum(static layer => layer.RemainingQty),
                    6,
                    MidpointRounding.AwayFromZero);
                if (availableQuantity >= baseQuantity && openCostLayerQuantity < baseQuantity)
                {
                    throw new InvalidOperationException(
                        $"Sales issue cannot post for '{item.Name}' because physical quantity is available but only {openCostLayerQuantity} has emitted cost-layer coverage. Review receipt valuation emission before outbound costing.");
                }

                var lineCostResult = item.DefaultCostingMethod switch
                {
                    InventoryCostingMethod.Fifo => ConsumeFifo(costLayers, baseQuantity, item.Name),
                    InventoryCostingMethod.MovingAverage => ConsumeMovingAverage(costLayers, baseQuantity, item.Name),
                    _ => throw new InvalidOperationException($"Unsupported inventory costing method '{item.DefaultCostingMethod}'.")
                };
                var quantityAfter = decimal.Round(balance.OnHandQty - baseQuantity, 6, MidpointRounding.AwayFromZero);
                var currentCostBalance = await LoadCurrentCostBalanceAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    line.ItemId,
                    line.WarehouseId,
                    cancellationToken);
                var costAfter = decimal.Round(currentCostBalance - lineCostResult.TotalCostBase, 6, MidpointRounding.AwayFromZero);
                var ledgerEntryId = Guid.NewGuid();
                var unitCostBase = baseQuantity == 0
                    ? 0m
                    : decimal.Round(lineCostResult.TotalCostBase / baseQuantity, 6, MidpointRounding.AwayFromZero);

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
                          1,
                          @unit_cost_tx,
                          @unit_cost_base,
                          @extended_cost_base,
                          @reason_code,
                          @memo
                        );
                        """;
                    insertLineCommand.Parameters.AddWithValue("id", lineId);
                    insertLineCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                    insertLineCommand.Parameters.AddWithValue("document_id", documentId);
                    insertLineCommand.Parameters.AddWithValue("line_no", line.LineNo);
                    insertLineCommand.Parameters.AddWithValue("item_id", line.ItemId);
                    insertLineCommand.Parameters.AddWithValue("warehouse_id", line.WarehouseId);
                    insertLineCommand.Parameters.AddWithValue("uom_code", normalizedUomCode);
                    insertLineCommand.Parameters.AddWithValue("quantity", line.Quantity);
                    insertLineCommand.Parameters.AddWithValue("base_quantity", baseQuantity);
                    insertLineCommand.Parameters.AddWithValue("currency_code", baseCurrencyCode);
                    insertLineCommand.Parameters.AddWithValue("unit_cost_tx", unitCostBase);
                    insertLineCommand.Parameters.AddWithValue("unit_cost_base", unitCostBase);
                    insertLineCommand.Parameters.AddWithValue("extended_cost_base", lineCostResult.TotalCostBase);
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
                    -baseQuantity,
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
                          'outbound',
                          'sales_issue',
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
                    insertLedgerCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                    insertLedgerCommand.Parameters.AddWithValue("item_id", line.ItemId);
                    insertLedgerCommand.Parameters.AddWithValue("warehouse_id", line.WarehouseId);
                    insertLedgerCommand.Parameters.AddWithValue("document_id", documentId);
                    insertLedgerCommand.Parameters.AddWithValue("document_line_id", lineId);
                    insertLedgerCommand.Parameters.AddWithValue("posting_date", request.PostingDate);
                    insertLedgerCommand.Parameters.AddWithValue("quantity_delta", -baseQuantity);
                    insertLedgerCommand.Parameters.AddWithValue("quantity_after", quantityAfter);
                    insertLedgerCommand.Parameters.AddWithValue("cost_amount_delta_base", -lineCostResult.TotalCostBase);
                    insertLedgerCommand.Parameters.AddWithValue("cost_amount_after_base", costAfter);
                    insertLedgerCommand.Parameters.AddWithValue("memo", BuildLedgerMemo(documentNumber, line.LineNo));
                    insertLedgerCommand.Parameters.AddWithValue("created_at", createdAt);
                    await insertLedgerCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var consumption in lineCostResult.Consumptions)
                {
                    await using var updateLayerCommand = connection.CreateCommand();
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
                    insertConsumptionCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
                    insertConsumptionCommand.Parameters.AddWithValue("issue_ledger_entry_id", ledgerEntryId);
                    insertConsumptionCommand.Parameters.AddWithValue("cost_layer_id", consumption.CostLayerId);
                    insertConsumptionCommand.Parameters.AddWithValue("consumed_qty", consumption.ConsumedQty);
                    insertConsumptionCommand.Parameters.AddWithValue("consumed_cost_base", consumption.ConsumedCostBase);
                    insertConsumptionCommand.Parameters.AddWithValue("created_at", createdAt);
                    await insertConsumptionCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                totalQuantity += baseQuantity;
                totalCostBase += lineCostResult.TotalCostBase;
            }

            var summary = new InventorySalesIssueSummary(
                documentId,
                request.CompanyId,
                documentNumber,
                "posted",
                request.PostingDate,
                request.CustomerId,
                customerDisplayName,
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
            throw new InvalidOperationException("Another inventory sales issue already uses the same company-scoped issue number.", ex);
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
        CompanyId companyId,
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string baseCurrencyCode || string.IsNullOrWhiteSpace(baseCurrencyCode))
        {
            throw new InvalidOperationException("Company base currency could not be found.");
        }

        return baseCurrencyCode.Trim().ToUpperInvariant();
    }

    private static async Task<string> LoadCustomerDisplayNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select display_name
            from customers
            where id = @customer_id
              and company_id = @company_id
              and is_active = true
            limit 1;
            """;
        command.Parameters.AddWithValue("customer_id", customerId);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string displayName || string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("The selected customer could not be found for this company or is inactive.");
        }

        return displayName.Trim();
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
            var summary = new InventoryManagedItemSummary(
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
            items[summary.Id] = summary;
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
            var summary = new InventoryManagedWarehouseSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
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
              and warehouse_id = @warehouse_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ItemWarehouseBalanceSnapshot(0m, 0m);
        }

        return new ItemWarehouseBalanceSnapshot(
            reader.GetFieldValue<decimal>(reader.GetOrdinal("on_hand_qty")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("reserved_qty")));
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
            select coalesce(sum(cost_amount_delta_base), 0)
            from inventory_ledger_entries
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
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
            order by layer_date asc, created_at asc, id asc;
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
            throw new InvalidOperationException($"Sales issue cannot post for '{itemName}' because open cost layers do not cover the outbound quantity. If physical quantity exists, receipt valuation cost-layer emission is incomplete and must be reviewed before outbound costing.");
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
            throw new InvalidOperationException($"Sales issue cannot post for '{itemName}' because open cost layers do not cover the outbound quantity. If physical quantity exists, receipt valuation cost-layer emission is incomplete and must be reviewed before outbound costing.");
        }

        var issueCostBase = totalRemainingQty == 0
            ? 0m
            : decimal.Round(issueQuantity * (totalRemainingCost / totalRemainingQty), 6, MidpointRounding.AwayFromZero);

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

                consumedCost = totalRemainingCost == 0
                    ? 0m
                    : decimal.Round(issueCostBase * (layer.RemainingCostBase / totalRemainingCost), 6, MidpointRounding.AwayFromZero);
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
            throw new InvalidOperationException($"Sales issue cannot post for '{itemName}' because open cost layers do not cover the outbound quantity. If physical quantity exists, receipt valuation cost-layer emission is incomplete and must be reviewed before outbound costing.");
        }

        return new IssueCostComputation(issueCostBase, consumptions);
    }

    private static async Task UpsertBalanceAsync(
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        command.Parameters.AddWithValue("quantity_delta", quantityDelta);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
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
                true,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))));
        }

        return warehouses;
    }

    private static async Task<(int LineCount, decimal TotalQuantity)> LoadInvoiceOutboundSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              count(*)::int as line_count,
              coalesce(sum(l.quantity), 0) as total_quantity
            from invoice_lines l
            where l.company_id = @company_id
              and l.invoice_id = @invoice_document_id
              and l.item_id is not null
              and l.warehouse_id is not null
              and l.uom_code is not null;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0m);
        }

        return (
            reader.GetInt32(reader.GetOrdinal("line_count")),
            reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")));
    }

    private static async Task<IReadOnlyList<InventoryInvoiceIssueHandoffLineSummary>> LoadInvoiceHandoffLineSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with invoice_groups as (
                select
                  l.item_id,
                  i.item_code,
                  i.name as item_name,
                  l.warehouse_id,
                  w.warehouse_code,
                  w.name as warehouse_name,
                  upper(l.uom_code) as uom_code,
                  count(*)::int as invoice_line_count,
                  coalesce(sum(l.quantity), 0) as invoice_quantity
                from invoice_lines l
                join invoices d
                  on d.id = l.invoice_id
                 and d.company_id = l.company_id
                join inventory_items i
                  on i.id = l.item_id
                 and i.company_id = l.company_id
                join inventory_warehouses w
                  on w.id = l.warehouse_id
                 and w.company_id = l.company_id
                where l.company_id = @company_id
                  and l.invoice_id = @invoice_document_id
                  and l.item_id is not null
                  and l.warehouse_id is not null
                  and l.uom_code is not null
                group by
                  l.item_id,
                  i.item_code,
                  i.name,
                  l.warehouse_id,
                  w.warehouse_code,
                  w.name,
                  upper(l.uom_code)
            ),
            issue_groups as (
                select
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code) as uom_code,
                  coalesce(sum(l.base_quantity), 0) as issued_quantity
                from inventory_documents d
                join inventory_document_lines l
                  on l.document_id = d.id
                 and l.company_id = d.company_id
                where d.company_id = @company_id
                  and d.document_type = 'sales_issue'
                  and d.source_module = 'ar_invoice'
                  and d.source_document_id = @invoice_document_id
                group by
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code)
            )
            select
              i.item_id,
              i.item_code,
              i.item_name,
              i.warehouse_id,
              i.warehouse_code,
              i.warehouse_name,
              i.uom_code,
              i.invoice_line_count,
              i.invoice_quantity,
              coalesce(s.issued_quantity, 0) as issued_quantity
            from invoice_groups i
            left join issue_groups s
              on s.item_id = i.item_id
             and s.warehouse_id = i.warehouse_id
             and s.uom_code = i.uom_code
            order by i.item_code, i.warehouse_code, i.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var rows = new List<InventoryInvoiceIssueHandoffLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("invoice_quantity")), 6, MidpointRounding.AwayFromZero);
            var issuedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("issued_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(invoiceQuantity - issuedQuantity, 6, MidpointRounding.AwayFromZero);
            var matchStatus =
                issuedQuantity <= 0m
                    ? "no_issue"
                    : remainingQuantity > 0m
                        ? "partially_issued"
                        : remainingQuantity == 0m
                            ? "fully_issued"
                            : "over_issued";

            rows.Add(new InventoryInvoiceIssueHandoffLineSummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetInt32(reader.GetOrdinal("invoice_line_count")),
                invoiceQuantity,
                issuedQuantity,
                remainingQuantity,
                matchStatus));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<InventorySalesIssueSummary>> LoadInvoiceAnchoredIssuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid invoiceDocumentId,
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
              d.counterparty_id as customer_id,
              coalesce(c.display_name, 'Unknown customer') as customer_display_name,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              coalesce(sum(l.extended_cost_base), 0) as total_cost_base,
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
            where d.company_id = @company_id
              and d.document_type = 'sales_issue'
              and d.source_module = 'ar_invoice'
              and d.source_document_id = @invoice_document_id
            group by
              d.id,
              d.company_id,
              d.document_number,
              d.source_document_number,
              d.status,
              d.posting_date,
              d.counterparty_id,
              c.display_name,
              d.created_at,
              d.posted_at,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var issues = new List<InventorySalesIssueSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            issues.Add(new InventorySalesIssueSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_display_name")),
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

        return issues;
    }

    private static async Task<IReadOnlyList<InventorySalesIssueSummary>> LoadRecentIssuesAsync(
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
              d.id,
              d.company_id,
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              d.status,
              d.posting_date,
              d.counterparty_id as customer_id,
              coalesce(c.display_name, 'Unknown customer') as customer_display_name,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              coalesce(sum(l.extended_cost_base), 0) as total_cost_base,
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
            where d.company_id = @company_id
              and d.document_type = 'sales_issue'
            group by
              d.id,
              d.company_id,
              d.document_number,
              d.source_document_number,
              d.status,
              d.posting_date,
              d.counterparty_id,
              c.display_name,
              d.created_at,
              d.posted_at,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var issues = new List<InventorySalesIssueSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            issues.Add(new InventorySalesIssueSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_display_name")),
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

        return issues;
    }

    private static string BuildIssueNumber(DateOnly postingDate) =>
        $"SI-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

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

    private sealed record ItemWarehouseBalanceSnapshot(
        decimal OnHandQty,
        decimal ReservedQty);
}
