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

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _foundationStore.EnsureSchemaAsync(cancellationToken);
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: true);
    }

    public async Task<InventorySalesIssueDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

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
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

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
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

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
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
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
                      posted_at,
                      idempotency_key
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
                      @posted_at,
                      @idempotency_key
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
                insertDocumentCommand.Parameters.AddWithValue(
                    "idempotency_key",
                    string.IsNullOrWhiteSpace(request.IdempotencyKey)
                        ? (object)DBNull.Value
                        : request.IdempotencyKey.Trim());
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
        catch (PostgresException ex) when (InventoryIdempotencyHelper.IsIdempotencyViolation(ex))
        {
            // PR-5 (C-4): retried POST hit the idempotency partial
            // unique index. See PostgreSqlInventoryReceiptStore for
            // the rationale.
            await transaction.RollbackAsync(cancellationToken);
            await InventoryIdempotencyHelper.ThrowReplayAsync(
                _connections, request.CompanyId, request.IdempotencyKey!.Trim(), cancellationToken);
            throw; // unreachable
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

    /// <summary>
    /// P0-2 (C2): undoes the subledger effects of a posted sales-issue
    /// as part of an invoice-reverse. The matching GL-side compensation
    /// is posted independently by <c>PostSalesIssueCogsReverseCommandHandler</c>;
    /// the two halves are independently idempotent so a partial-commit
    /// recovery on retry leaves both sides eventually consistent.
    /// </summary>
    public async Task<InventorySalesIssueReverseSummary> ReverseForInvoiceAsync(
        CompanyId companyId,
        Guid salesIssueDocumentId,
        Guid invoiceId,
        UserId actorId,
        CancellationToken cancellationToken)
    {
        if (salesIssueDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Sales-issue document id is required.", nameof(salesIssueDocumentId));
        }

        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Idempotency probe + status guard. FOR UPDATE locks the
            // inventory_documents row so two concurrent invoice-reverse
            // attempts can't both pass the reversed_at check.
            string status;
            DateTimeOffset? existingReversedAt = null;
            DateOnly postingDate;
            await using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText =
                    """
                    select status, reversed_at, posting_date
                    from inventory_documents
                    where company_id = @company_id
                      and id = @sales_issue_id
                      and document_type = 'sales_issue'
                    for update;
                    """;
                probe.Parameters.AddWithValue("company_id", companyId.Value);
                probe.Parameters.AddWithValue("sales_issue_id", salesIssueDocumentId);

                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Sales-issue {salesIssueDocumentId:D} was not found for company {companyId.Value:D}.");
                }
                status = reader.GetString(0);
                existingReversedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                postingDate = reader.GetFieldValue<DateOnly>(2);
            }

            if (existingReversedAt.HasValue)
            {
                await transaction.CommitAsync(cancellationToken);
                return new InventorySalesIssueReverseSummary(
                    SalesIssueDocumentId: salesIssueDocumentId,
                    InvoiceId: invoiceId,
                    AlreadyReversed: true,
                    LineCount: 0,
                    TotalQuantityRestored: 0m,
                    TotalCostBaseRestored: 0m,
                    ReversedAt: existingReversedAt.Value);
            }

            if (!string.Equals(status, "posted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Sales-issue {salesIssueDocumentId:D} cannot be reversed because its status is '{status}'.");
            }

            // 2. Restore inventory_cost_layers: add back consumed qty +
            // cost. The forward post wrote inventory_layer_consumptions
            // rows we deliberately keep for the audit trail; the
            // remaining_qty / remaining_cost_base columns on
            // inventory_cost_layers are the live state and are what
            // gets restored here.
            await using (var restoreLayers = connection.CreateCommand())
            {
                restoreLayers.Transaction = transaction;
                restoreLayers.CommandText =
                    """
                    with target_consumptions as (
                      select c.cost_layer_id,
                             sum(c.consumed_qty)        as restore_qty,
                             sum(c.consumed_cost_base)  as restore_cost_base
                      from inventory_layer_consumptions c
                      join inventory_ledger_entries le on le.id = c.issue_ledger_entry_id
                      where c.company_id = @company_id
                        and le.company_id = @company_id
                        and le.document_id = @sales_issue_id
                      group by c.cost_layer_id
                    )
                    update inventory_cost_layers cl
                    set remaining_qty = cl.remaining_qty + t.restore_qty,
                        remaining_cost_base = cl.remaining_cost_base + t.restore_cost_base
                    from target_consumptions t
                    where cl.id = t.cost_layer_id;
                    """;
                restoreLayers.Parameters.AddWithValue("company_id", companyId.Value);
                restoreLayers.Parameters.AddWithValue("sales_issue_id", salesIssueDocumentId);
                await restoreLayers.ExecuteNonQueryAsync(cancellationToken);
            }

            // 3. Load each original outbound ledger entry so we know
            // (item_id, warehouse_id, quantity_delta, cost_amount_delta_base)
            // to reverse. We loop one-at-a-time because each row needs to
            // produce (a) a matched inbound ledger entry and (b) an
            // upsert on item_warehouse_balances.
            var originalEntries = new List<OriginalLedgerEntry>();
            await using (var loadOriginals = connection.CreateCommand())
            {
                loadOriginals.Transaction = transaction;
                loadOriginals.CommandText =
                    """
                    select id, item_id, warehouse_id, document_line_id,
                           quantity_delta, cost_amount_delta_base
                    from inventory_ledger_entries
                    where company_id = @company_id
                      and document_id = @sales_issue_id
                      and movement_direction = 'outbound'
                      and movement_type = 'sales_issue'
                    order by created_at, id;
                    """;
                loadOriginals.Parameters.AddWithValue("company_id", companyId.Value);
                loadOriginals.Parameters.AddWithValue("sales_issue_id", salesIssueDocumentId);

                await using var reader = await loadOriginals.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    originalEntries.Add(new OriginalLedgerEntry(
                        Id: reader.GetGuid(0),
                        ItemId: reader.GetGuid(1),
                        WarehouseId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                        DocumentLineId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                        QuantityDelta: reader.GetDecimal(4),
                        CostAmountDeltaBase: reader.GetDecimal(5)));
                }
            }

            if (originalEntries.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Sales-issue {salesIssueDocumentId:D} has no outbound ledger entries — cannot reverse.");
            }

            var reversedAt = DateTimeOffset.UtcNow;
            var totalQtyRestored = 0m;
            var totalCostRestored = 0m;

            foreach (var entry in originalEntries)
            {
                // The forward leg wrote negative quantity_delta + negative
                // cost_amount_delta_base. The reverse leg writes the
                // negated values so the per-row sum nets to zero.
                var restoreQty = -entry.QuantityDelta;
                var restoreCost = -entry.CostAmountDeltaBase;
                totalQtyRestored += restoreQty;
                totalCostRestored += restoreCost;

                // Sales-issue lines always pin a warehouse, but the ledger
                // schema allows null. Skip the balance upsert defensively
                // when the original entry was warehouse-less (e.g. a
                // future drop-ship-style movement) — those don't affect
                // item_warehouse_balances either way.
                if (entry.WarehouseId is { } warehouseId)
                {
                    // Upsert item_warehouse_balances: add back to on_hand_qty.
                    // The (company_id, item_id, warehouse_id) unique index
                    // is the conflict target so two concurrent reverses
                    // serialize at INSERT time.
                    await using var upsertBalance = connection.CreateCommand();
                    upsertBalance.Transaction = transaction;
                    upsertBalance.CommandText =
                        """
                        insert into item_warehouse_balances (
                          id, company_id, item_id, warehouse_id,
                          on_hand_qty, reserved_qty, in_transit_out_qty, in_transit_in_qty,
                          updated_at
                        )
                        values (
                          gen_random_uuid(), @company_id, @item_id, @warehouse_id,
                          @restore_qty, 0, 0, 0,
                          now()
                        )
                        on conflict (company_id, item_id, warehouse_id)
                        do update
                          set on_hand_qty = item_warehouse_balances.on_hand_qty + excluded.on_hand_qty,
                              updated_at = now();
                        """;
                    upsertBalance.Parameters.AddWithValue("company_id", companyId.Value);
                    upsertBalance.Parameters.AddWithValue("item_id", entry.ItemId);
                    upsertBalance.Parameters.AddWithValue("warehouse_id", warehouseId);
                    upsertBalance.Parameters.AddWithValue("restore_qty", restoreQty);
                    await upsertBalance.ExecuteNonQueryAsync(cancellationToken);
                }

                // Compute the post-restore running totals for the
                // compensating ledger entry. quantity_after / cost_after
                // are SUM-of-deltas over inventory_ledger_entries — these
                // are the canonical running totals, not item_warehouse_balances
                // (which only tracks qty, not cost).
                decimal quantityAfter;
                decimal costAfter;
                await using (var readRunningTotals = connection.CreateCommand())
                {
                    readRunningTotals.Transaction = transaction;
                    readRunningTotals.CommandText =
                        """
                        select coalesce(sum(quantity_delta), 0),
                               coalesce(sum(cost_amount_delta_base), 0)
                        from inventory_ledger_entries
                        where company_id = @company_id
                          and item_id = @item_id
                          and warehouse_id is not distinct from @warehouse_id;
                        """;
                    readRunningTotals.Parameters.AddWithValue("company_id", companyId.Value);
                    readRunningTotals.Parameters.AddWithValue("item_id", entry.ItemId);
                    readRunningTotals.Parameters.AddWithValue(
                        "warehouse_id",
                        (object?)entry.WarehouseId ?? DBNull.Value);
                    await using var reader = await readRunningTotals.ExecuteReaderAsync(cancellationToken);
                    await reader.ReadAsync(cancellationToken);
                    // After this restore commits its insert below, the totals
                    // will reflect the restore. Pre-compute that here.
                    quantityAfter = reader.GetDecimal(0) + restoreQty;
                    costAfter = reader.GetDecimal(1) + restoreCost;
                }

                // Write the compensating inbound ledger entry. movement_type
                // = 'customer_return_receipt' is the closest valid inbound
                // value in the CHECK constraint; the memo + document_id link
                // back to the original sales-issue so reports can group the
                // pair. Adding a dedicated 'sales_issue_reverse' enum entry
                // would require dropping/recreating the named CHECK
                // constraint — deferred to a follow-up cleanup batch.
                await using (var insertReverseLedger = connection.CreateCommand())
                {
                    insertReverseLedger.Transaction = transaction;
                    insertReverseLedger.CommandText =
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
                          gen_random_uuid(),
                          @company_id,
                          @item_id,
                          @warehouse_id,
                          @document_id,
                          @document_line_id,
                          'inbound',
                          'customer_return_receipt',
                          @posting_date,
                          @quantity_delta,
                          @quantity_after,
                          @cost_amount_delta,
                          @cost_amount_after,
                          @memo,
                          @created_at
                        );
                        """;
                    insertReverseLedger.Parameters.AddWithValue("company_id", companyId.Value);
                    insertReverseLedger.Parameters.AddWithValue("item_id", entry.ItemId);
                    insertReverseLedger.Parameters.AddWithValue(
                        "warehouse_id",
                        (object?)entry.WarehouseId ?? DBNull.Value);
                    insertReverseLedger.Parameters.AddWithValue("document_id", salesIssueDocumentId);
                    insertReverseLedger.Parameters.AddWithValue(
                        "document_line_id",
                        (object?)entry.DocumentLineId ?? DBNull.Value);
                    insertReverseLedger.Parameters.AddWithValue("posting_date", postingDate);
                    insertReverseLedger.Parameters.AddWithValue("quantity_delta", restoreQty);
                    insertReverseLedger.Parameters.AddWithValue("quantity_after", quantityAfter);
                    insertReverseLedger.Parameters.AddWithValue("cost_amount_delta", restoreCost);
                    insertReverseLedger.Parameters.AddWithValue("cost_amount_after", costAfter);
                    insertReverseLedger.Parameters.AddWithValue(
                        "memo",
                        $"P0-2 compensation — invoice reverse {invoiceId:D} (actor {actorId.Value})");
                    insertReverseLedger.Parameters.AddWithValue("created_at", reversedAt);
                    await insertReverseLedger.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // 4. Stamp the idempotency marker.
            await using (var markReversed = connection.CreateCommand())
            {
                markReversed.Transaction = transaction;
                markReversed.CommandText =
                    """
                    update inventory_documents
                    set reversed_at = @reversed_at
                    where company_id = @company_id
                      and id = @sales_issue_id
                      and reversed_at is null;
                    """;
                markReversed.Parameters.AddWithValue("company_id", companyId.Value);
                markReversed.Parameters.AddWithValue("sales_issue_id", salesIssueDocumentId);
                markReversed.Parameters.AddWithValue("reversed_at", reversedAt);
                var affected = await markReversed.ExecuteNonQueryAsync(cancellationToken);
                if (affected != 1)
                {
                    throw new InvalidOperationException(
                        $"Failed to mark sales-issue {salesIssueDocumentId:D} as reversed — concurrent reverse may have raced ahead.");
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return new InventorySalesIssueReverseSummary(
                SalesIssueDocumentId: salesIssueDocumentId,
                InvoiceId: invoiceId,
                AlreadyReversed: false,
                LineCount: originalEntries.Count,
                TotalQuantityRestored: decimal.Round(totalQtyRestored, 6, MidpointRounding.AwayFromZero),
                TotalCostBaseRestored: decimal.Round(totalCostRestored, 6, MidpointRounding.AwayFromZero),
                ReversedAt: reversedAt);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private readonly record struct OriginalLedgerEntry(
        Guid Id,
        Guid ItemId,
        Guid? WarehouseId,
        Guid? DocumentLineId,
        decimal QuantityDelta,
        decimal CostAmountDeltaBase);

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
                "Inventory issue schema has not been installed. Apply database migrations before using inventory issue features.");
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
                  and column_name = 'document_number');
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
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
        // P0-4 (C4): row-level FOR UPDATE serializes concurrent sales-issue
        // posts against the same (company_id, item_id, warehouse_id) so two
        // writers cannot both pass the negative-stock guard at line 365 and
        // then both UPSERT-decrement. Without this, READ COMMITTED lets
        // their SELECTs both see e.g. 5 on-hand and both ship 4, leaving
        // -3 on-hand. The downstream INSERT ... ON CONFLICT serializes via
        // the (company_id, item_id, warehouse_id) unique index for the
        // empty-row case where FOR UPDATE locks nothing.
        command.CommandText =
            """
            select
              coalesce(on_hand_qty, 0) as on_hand_qty,
              coalesce(reserved_qty, 0) as reserved_qty
            from item_warehouse_balances
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id
            limit 1
            for update;
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
