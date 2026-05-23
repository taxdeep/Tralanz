using Citus.Modules.Inventory.Application.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlReceiptInventoryActivationStore : IReceiptInventoryActivationStore
{
    private const string ReceiptsTableName = "receipts";
    private const string ReceiptLinesTableName = "receipt_lines";
    private const string ActivationLinesTableName = "receipt_inventory_activation_lines";
    private const string ActivationFailuresTableName = "receipt_inventory_activation_failures";

    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly InventoryReceiptExecutionContextAccessor _executionContextAccessor;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlReceiptInventoryActivationStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore,
        InventoryReceiptExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _foundationStore.EnsureSchemaAsync(cancellationToken);
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: true);
    }

    public async Task ValidateCanActivateAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

        var receipt = await LoadReceiptRecordAsync(connection, null, companyId, receiptDocumentId, cancellationToken);
        ValidateReceiptForActivation(receipt, allowDraft: true);

        var itemMap = await LoadItemMapAsync(
            connection,
            null,
            companyId,
            receipt.Lines.Select(static line => line.ItemId).Distinct().ToArray(),
            cancellationToken);
        var warehouseMap = await LoadWarehouseMapAsync(
            connection,
            null,
            companyId,
            [receipt.WarehouseId],
            cancellationToken);

        ValidateInventoryAnchors(receipt, itemMap, warehouseMap);
    }

    public async Task<ReceiptInventoryActivationSummary> ActivatePostedReceiptAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        // M4 (P2-10): join the workflow's ambient tx when present, so
        // activation + valuation + emission commit as one unit and a
        // valuation/emission failure rolls back the activation rows.
        if (_executionContextAccessor.Current is { } ambient)
        {
            await EnsureSchemaAsync(ambient.Connection, cancellationToken, allowCreate: false);
            return await ActivatePostedReceiptCoreAsync(
                ambient.Connection,
                ambient.Transaction,
                companyId,
                userId,
                receiptDocumentId,
                cancellationToken);
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = await ActivatePostedReceiptCoreAsync(
                connection,
                transaction,
                companyId,
                userId,
                receiptDocumentId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original failure; rollback can fail
                // if the connection is already broken.
            }
            throw;
        }
    }

    private async Task<ReceiptInventoryActivationSummary> ActivatePostedReceiptCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        var receipt = await LoadReceiptRecordAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
        ValidateReceiptForActivation(receipt, allowDraft: false);

        var existingActivations = await LoadActivationRowsAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
        if (existingActivations.Count > 0)
        {
            var activatedLineNumbers = existingActivations
                .Select(static row => row.ReceiptLineNumber)
                .Distinct()
                .OrderBy(static value => value)
                .ToArray();
            var receiptLineNumbers = receipt.Lines
                .Select(static line => line.LineNumber)
                .OrderBy(static value => value)
                .ToArray();

            if (!activatedLineNumbers.SequenceEqual(receiptLineNumbers))
            {
                throw new InvalidOperationException("Receipt inventory activation is in an inconsistent partial state and requires investigation before retry.");
            }

            var existingSummary = await LoadReceiptActivationSummaryAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
            return existingSummary ?? throw new InvalidOperationException("Receipt inventory activation summary could not be loaded.");
        }

        var itemMap = await LoadItemMapAsync(
            connection,
            transaction,
            companyId,
            receipt.Lines.Select(static line => line.ItemId).Distinct().ToArray(),
            cancellationToken);
        var warehouseMap = await LoadWarehouseMapAsync(
            connection,
            transaction,
            companyId,
            [receipt.WarehouseId],
            cancellationToken);

        ValidateInventoryAnchors(receipt, itemMap, warehouseMap);

        var activatedAt = DateTimeOffset.UtcNow;
        var inventoryDocumentId = Guid.NewGuid();
        var inventoryDocumentNumber = BuildActivationDocumentNumber(receipt.ReceiptDate);

        await InsertInventoryDocumentAsync(
            connection,
            transaction,
            companyId,
            userId,
            receipt,
            inventoryDocumentId,
            inventoryDocumentNumber,
            activatedAt,
            cancellationToken);

        foreach (var line in receipt.Lines.OrderBy(static line => line.LineNumber))
        {
            await ActivateLineAsync(
                connection,
                transaction,
                companyId,
                userId,
                receipt,
                line,
                inventoryDocumentId,
                activatedAt,
                cancellationToken);
        }

        await ClearActivationFailuresAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
        var summary = await LoadReceiptActivationSummaryAsync(connection, transaction, companyId, receiptDocumentId, cancellationToken);
        return summary ?? throw new InvalidOperationException("Receipt inventory activation summary could not be loaded.");
    }

    public async Task RecordActivationFailureAsync(
        CompanyId companyId,
        UserId userId,
        Guid receiptDocumentId,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            insert into {ActivationFailuresTableName} (
              id,
              company_id,
              receipt_id,
              failure_message,
              recorded_by_user_id,
              recorded_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @receipt_id,
              @failure_message,
              @recorded_by_user_id,
              now()
            )
            on conflict (company_id, receipt_id)
            do update
              set failure_message = excluded.failure_message,
                  recorded_by_user_id = excluded.recorded_by_user_id,
                  recorded_at = now();
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        command.Parameters.AddWithValue("failure_message", string.IsNullOrWhiteSpace(failureMessage) ? "Receipt activation failed." : failureMessage.Trim());
        command.Parameters.AddWithValue("recorded_by_user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReceiptInventoryActivationSummary?> GetReceiptActivationSummaryAsync(
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        return await LoadReceiptActivationSummaryAsync(connection, null, companyId, receiptDocumentId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, ReceiptInventoryActivationSummary>> GetReceiptActivationSummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        if (receiptDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, ReceiptInventoryActivationSummary>();
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        return await LoadReceiptActivationSummariesAsync(connection, null, companyId, receiptDocumentIds.Distinct().ToArray(), cancellationToken);
    }

    private static void ValidateReceiptForActivation(ReceiptActivationRecord receipt, bool allowDraft)
    {
        if (receipt.Lines.Count == 0)
        {
            throw new InvalidOperationException("Receipt activation requires at least one receipt line.");
        }

        if (allowDraft)
        {
            if (!string.Equals(receipt.Status, "draft", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(receipt.Status, "posted", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only draft or posted receipts can enter inventory activation validation.");
            }

            return;
        }

        if (!string.Equals(receipt.Status, "posted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only posted receipts can activate inventory truth.");
        }
    }

    private static void ValidateInventoryAnchors(
        ReceiptActivationRecord receipt,
        IReadOnlyDictionary<Guid, ReceiptActivationItemRecord> itemMap,
        IReadOnlyDictionary<Guid, ReceiptActivationWarehouseRecord> warehouseMap)
    {
        if (!warehouseMap.ContainsKey(receipt.WarehouseId))
        {
            throw new InvalidOperationException("Receipt inventory activation requires an active warehouse in this company.");
        }

        foreach (var line in receipt.Lines)
        {
            if (!itemMap.TryGetValue(line.ItemId, out var item))
            {
                throw new InvalidOperationException("Receipt inventory activation requires active inventory items in this company.");
            }

            if (!string.Equals(item.ItemKind, "stock", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Receipt inventory activation only supports stock items. '{item.Name}' is not a stock item.");
            }

            if (!string.Equals(item.ManageInventoryMethod, "manage_stock", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Receipt inventory activation currently supports only warehouse-managed stock items. '{item.Name}' is not on that path.");
            }

            if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Receipt line UOM must match the stock UOM for '{item.Name}'.");
            }
        }
    }

    private static async Task InsertInventoryDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        ReceiptActivationRecord receipt,
        Guid inventoryDocumentId,
        string inventoryDocumentNumber,
        DateTimeOffset activatedAt,
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
              'purchase_receipt',
              'posted',
              'inbound',
              @posting_date,
              'receipt_document',
              @source_document_id,
              @source_document_number,
              @counterparty_id,
              @memo,
              @created_by_user_id,
              @created_at,
              @posted_at
            );
            """;
        command.Parameters.AddWithValue("id", inventoryDocumentId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_number", inventoryDocumentNumber);
        command.Parameters.AddWithValue("posting_date", receipt.ReceiptDate);
        command.Parameters.AddWithValue("source_document_id", receipt.ReceiptId);
        command.Parameters.AddWithValue("source_document_number", receipt.DisplayNumber);
        command.Parameters.AddWithValue("counterparty_id", receipt.VendorId);
        command.Parameters.AddWithValue("memo", ToDbValue(BuildActivationMemo(receipt)));
        command.Parameters.AddWithValue("created_by_user_id", userId.Value);
        command.Parameters.AddWithValue("created_at", activatedAt);
        command.Parameters.AddWithValue("posted_at", activatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ActivateLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        ReceiptActivationRecord receipt,
        ReceiptActivationLineRecord line,
        Guid inventoryDocumentId,
        DateTimeOffset activatedAt,
        CancellationToken cancellationToken)
    {
        var currentOnHand = await LoadCurrentOnHandAsync(connection, transaction, companyId, line.ItemId, receipt.WarehouseId, cancellationToken);
        var currentCostBalance = await LoadCurrentCostBalanceAsync(connection, transaction, companyId, line.ItemId, receipt.WarehouseId, cancellationToken);
        var quantityAfter = Round6(currentOnHand + line.Quantity);
        var inventoryDocumentLineId = Guid.NewGuid();
        var ledgerEntryId = Guid.NewGuid();

        await InsertInventoryDocumentLineAsync(
            connection,
            transaction,
            companyId,
            inventoryDocumentId,
            receipt,
            line,
            inventoryDocumentLineId,
            cancellationToken);
        await InsertInventoryLedgerEntryAsync(
            connection,
            transaction,
            companyId,
            inventoryDocumentId,
            inventoryDocumentLineId,
            ledgerEntryId,
            receipt,
            line,
            quantityAfter,
            currentCostBalance,
            activatedAt,
            cancellationToken);
        await UpsertBalanceAsync(connection, transaction, companyId, line.ItemId, receipt.WarehouseId, line.Quantity, cancellationToken);
        await InsertActivationRowAsync(
            connection,
            transaction,
            companyId,
            userId,
            inventoryDocumentId,
            inventoryDocumentLineId,
            receipt,
            line,
            activatedAt,
            cancellationToken);
    }

    private static async Task InsertInventoryDocumentLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid inventoryDocumentId,
        ReceiptActivationRecord receipt,
        ReceiptActivationLineRecord line,
        Guid inventoryDocumentLineId,
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
              null,
              null,
              null,
              null,
              null,
              'receipt_activation',
              @memo
            );
            """;
        command.Parameters.AddWithValue("id", inventoryDocumentLineId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", inventoryDocumentId);
        command.Parameters.AddWithValue("line_no", line.LineNumber);
        command.Parameters.AddWithValue("item_id", line.ItemId);
        command.Parameters.AddWithValue("warehouse_id", receipt.WarehouseId);
        command.Parameters.AddWithValue("uom_code", line.UomCode);
        command.Parameters.AddWithValue("quantity", line.Quantity);
        command.Parameters.AddWithValue("base_quantity", line.Quantity);
        command.Parameters.AddWithValue("memo", ToDbValue(BuildActivationLineMemo(receipt.DisplayNumber, line.LineNumber)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInventoryLedgerEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid inventoryDocumentId,
        Guid inventoryDocumentLineId,
        Guid ledgerEntryId,
        ReceiptActivationRecord receipt,
        ReceiptActivationLineRecord line,
        decimal quantityAfter,
        decimal currentCostBalance,
        DateTimeOffset activatedAt,
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
              'inbound',
              'purchase_receipt',
              @posting_date,
              @quantity_delta,
              @quantity_after,
              0,
              @cost_amount_after_base,
              @memo,
              @created_at
            );
            """;
        command.Parameters.AddWithValue("id", ledgerEntryId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", line.ItemId);
        command.Parameters.AddWithValue("warehouse_id", receipt.WarehouseId);
        command.Parameters.AddWithValue("document_id", inventoryDocumentId);
        command.Parameters.AddWithValue("document_line_id", inventoryDocumentLineId);
        command.Parameters.AddWithValue("posting_date", receipt.ReceiptDate);
        command.Parameters.AddWithValue("quantity_delta", line.Quantity);
        command.Parameters.AddWithValue("quantity_after", quantityAfter);
        command.Parameters.AddWithValue("cost_amount_after_base", currentCostBalance);
        command.Parameters.AddWithValue("memo", ToDbValue(BuildActivationLineMemo(receipt.DisplayNumber, line.LineNumber)));
        command.Parameters.AddWithValue("created_at", activatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActivationRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid inventoryDocumentId,
        Guid inventoryDocumentLineId,
        ReceiptActivationRecord receipt,
        ReceiptActivationLineRecord line,
        DateTimeOffset activatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            insert into {ActivationLinesTableName} (
              id,
              company_id,
              receipt_id,
              receipt_line_number,
              inventory_document_id,
              inventory_document_line_id,
              item_id,
              warehouse_id,
              uom_code,
              activated_quantity,
              activated_by_user_id,
              activated_at
            )
            values (
              @id,
              @company_id,
              @receipt_id,
              @receipt_line_number,
              @inventory_document_id,
              @inventory_document_line_id,
              @item_id,
              @warehouse_id,
              @uom_code,
              @activated_quantity,
              @activated_by_user_id,
              @activated_at
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receipt.ReceiptId);
        command.Parameters.AddWithValue("receipt_line_number", line.LineNumber);
        command.Parameters.AddWithValue("inventory_document_id", inventoryDocumentId);
        command.Parameters.AddWithValue("inventory_document_line_id", inventoryDocumentLineId);
        command.Parameters.AddWithValue("item_id", line.ItemId);
        command.Parameters.AddWithValue("warehouse_id", receipt.WarehouseId);
        command.Parameters.AddWithValue("uom_code", line.UomCode);
        command.Parameters.AddWithValue("activated_quantity", line.Quantity);
        command.Parameters.AddWithValue("activated_by_user_id", userId.Value);
        command.Parameters.AddWithValue("activated_at", activatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ReceiptInventoryActivationSummary?> LoadReceiptActivationSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadReceiptActivationSummariesAsync(connection, transaction, companyId, [receiptDocumentId], cancellationToken);
        return summaries.TryGetValue(receiptDocumentId, out var summary) ? summary : null;
    }

    private static async Task<IReadOnlyDictionary<Guid, ReceiptInventoryActivationSummary>> LoadReceiptActivationSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] receiptDocumentIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with requested_receipts as (
              select unnest(@receipt_document_ids::uuid[]) as receipt_document_id
            ),
            receipt_groups as (
              select
                r.id as receipt_document_id,
                r.status as receipt_status,
                count(l.id)::int as receipt_line_count,
                coalesce(sum(l.quantity), 0)::numeric(18,6) as total_quantity
              from {ReceiptsTableName} r
              left join {ReceiptLinesTableName} l
                on l.company_id = r.company_id
               and l.receipt_id = r.id
              where r.company_id = @company_id
                and r.id = any(@receipt_document_ids)
              group by r.id, r.status
            ),
            activation_groups as (
              select
                a.receipt_id as receipt_document_id,
                min(a.inventory_document_id) as inventory_document_id,
                count(*)::int as activated_line_count,
                coalesce(sum(a.activated_quantity), 0)::numeric(18,6) as activated_quantity,
                max(a.activated_at) as activated_at
              from {ActivationLinesTableName} a
              where a.company_id = @company_id
                and a.receipt_id = any(@receipt_document_ids)
              group by a.receipt_id
            ),
            failure_groups as (
              select distinct on (f.receipt_id)
                f.receipt_id as receipt_document_id,
                f.failure_message,
                f.recorded_at
              from {ActivationFailuresTableName} f
              where f.company_id = @company_id
                and f.receipt_id = any(@receipt_document_ids)
              order by f.receipt_id, f.recorded_at desc
            )
            select
              rr.receipt_document_id,
              rg.receipt_status,
              coalesce(rg.receipt_line_count, 0) as receipt_line_count,
              coalesce(rg.total_quantity, 0)::numeric(18,6) as total_quantity,
              ag.inventory_document_id,
              coalesce(ag.activated_line_count, 0) as activated_line_count,
              coalesce(ag.activated_quantity, 0)::numeric(18,6) as activated_quantity,
              ag.activated_at,
              fg.failure_message,
              fg.recorded_at as failure_recorded_at
            from requested_receipts rr
            left join receipt_groups rg
              on rg.receipt_document_id = rr.receipt_document_id
            left join activation_groups ag
              on ag.receipt_document_id = rr.receipt_document_id
            left join failure_groups fg
              on fg.receipt_document_id = rr.receipt_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("receipt_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = receiptDocumentIds
        });
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var summaries = new Dictionary<Guid, ReceiptInventoryActivationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var receiptDocumentId = reader.GetGuid(reader.GetOrdinal("receipt_document_id"));
            var receiptStatus = reader.IsDBNull(reader.GetOrdinal("receipt_status"))
                ? "missing"
                : reader.GetString(reader.GetOrdinal("receipt_status"));
            var receiptLineCount = reader.GetInt32(reader.GetOrdinal("receipt_line_count"));
            var activatedLineCount = reader.GetInt32(reader.GetOrdinal("activated_line_count"));
            var lastFailureMessage = reader.IsDBNull(reader.GetOrdinal("failure_message"))
                ? null
                : reader.GetString(reader.GetOrdinal("failure_message"));
            var activationStatus = ReceiptInventoryActivationStatusPolicy.Resolve(
                receiptStatus,
                receiptLineCount,
                activatedLineCount,
                !string.IsNullOrWhiteSpace(lastFailureMessage));

            summaries[receiptDocumentId] = new ReceiptInventoryActivationSummary(
                receiptDocumentId,
                receiptStatus,
                activationStatus,
                reader.IsDBNull(reader.GetOrdinal("inventory_document_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("inventory_document_id")),
                receiptLineCount,
                activatedLineCount,
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity"))),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("activated_quantity"))),
                reader.IsDBNull(reader.GetOrdinal("activated_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("activated_at")),
                lastFailureMessage,
                reader.IsDBNull(reader.GetOrdinal("failure_recorded_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("failure_recorded_at")));
        }

        return summaries;
    }

    private static async Task<ReceiptActivationRecord> LoadReceiptRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var headerCommand = connection.CreateCommand();
        headerCommand.Transaction = transaction;
        headerCommand.CommandText =
            $"""
            select
              r.id,
              r.receipt_number,
              r.status,
              r.vendor_id,
              r.warehouse_id,
              r.receipt_date,
              r.memo,
              r.posted_at
            from {ReceiptsTableName} r
            where r.company_id = @company_id
              and r.id = @receipt_document_id
            limit 1;
            """;
        headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        headerCommand.Parameters.AddWithValue("receipt_document_id", receiptDocumentId);

        string displayNumber;
        string status;
        Guid vendorId;
        Guid warehouseId;
        DateOnly receiptDate;
        string? memo;
        DateTimeOffset? postedAt;

        await using (var reader = await headerCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Receipt document was not found in the active company context.");
            }

            displayNumber = reader.GetString(reader.GetOrdinal("receipt_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            vendorId = reader.GetGuid(reader.GetOrdinal("vendor_id"));
            warehouseId = reader.GetGuid(reader.GetOrdinal("warehouse_id"));
            receiptDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
            postedAt = reader.IsDBNull(reader.GetOrdinal("posted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"));
        }

        await using var lineCommand = connection.CreateCommand();
        lineCommand.Transaction = transaction;
        lineCommand.CommandText =
            $"""
            select
              line_number,
              item_id,
              quantity,
              uom_code
            from {ReceiptLinesTableName}
            where company_id = @company_id
              and receipt_id = @receipt_document_id
            order by line_number asc;
            """;
        lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
        lineCommand.Parameters.AddWithValue("receipt_document_id", receiptDocumentId);

        var lines = new List<ReceiptActivationLineRecord>();
        await using (var reader = await lineCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ReceiptActivationLineRecord(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("item_id")),
                    Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("quantity"))),
                    reader.GetString(reader.GetOrdinal("uom_code"))));
            }
        }

        return new ReceiptActivationRecord(
            receiptDocumentId,
            displayNumber,
            status,
            vendorId,
            warehouseId,
            receiptDate,
            memo,
            postedAt,
            lines);
    }

    private static async Task<IReadOnlyList<ReceiptActivationRow>> LoadActivationRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select
              receipt_line_number,
              inventory_document_id,
              inventory_document_line_id,
              activated_quantity,
              activated_at
            from {ActivationLinesTableName}
            where company_id = @company_id
              and receipt_id = @receipt_document_id
            order by receipt_line_number asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_document_id", receiptDocumentId);

        var rows = new List<ReceiptActivationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ReceiptActivationRow(
                reader.GetInt32(reader.GetOrdinal("receipt_line_number")),
                reader.GetGuid(reader.GetOrdinal("inventory_document_id")),
                reader.GetGuid(reader.GetOrdinal("inventory_document_line_id")),
                Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("activated_quantity"))),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("activated_at"))));
        }

        return rows;
    }

    private static async Task<Dictionary<Guid, ReceiptActivationItemRecord>> LoadItemMapAsync(
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
              name,
              item_kind,
              stock_uom_code,
              manage_inventory_method
            from inventory_items
            where company_id = @company_id
              and is_active = true
              and id = any(@item_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_ids", itemIds.ToArray());

        var items = new Dictionary<Guid, ReceiptActivationItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items[reader.GetGuid(reader.GetOrdinal("id"))] = new ReceiptActivationItemRecord(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("item_kind")),
                reader.IsDBNull(reader.GetOrdinal("stock_uom_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("stock_uom_code")),
                reader.GetString(reader.GetOrdinal("manage_inventory_method")));
        }

        return items;
    }

    private static async Task<Dictionary<Guid, ReceiptActivationWarehouseRecord>> LoadWarehouseMapAsync(
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
              warehouse_code,
              name
            from inventory_warehouses
            where company_id = @company_id
              and is_active = true
              and id = any(@warehouse_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("warehouse_ids", warehouseIds.ToArray());

        var warehouses = new Dictionary<Guid, ReceiptActivationWarehouseRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            warehouses[reader.GetGuid(reader.GetOrdinal("id"))] = new ReceiptActivationWarehouseRecord(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")));
        }

        return warehouses;
    }

    private static async Task<decimal> LoadCurrentOnHandAsync(
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
            select coalesce(on_hand_qty, 0)
            from item_warehouse_balances
            where company_id = @company_id
              and item_id = @item_id
              and warehouse_id = @warehouse_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("warehouse_id", warehouseId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
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

    private static async Task ClearActivationFailuresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            delete from {ActivationFailuresTableName}
            where company_id = @company_id
              and receipt_id = @receipt_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                "Receipt inventory activation schema has not been installed. Apply database migrations before activating receipts.");
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
                $"""
                alter table inventory_documents
                  add column if not exists document_number text null;

                create unique index if not exists ux_inventory_documents_company_document_number
                  on inventory_documents (company_id, lower(document_number))
                  where document_number is not null;

                create table if not exists {ActivationLinesTableName} (
                  id uuid primary key default gen_random_uuid(),
                  company_id char(7) not null references companies(id) on delete cascade,
                  receipt_id uuid not null,
                  receipt_line_number integer not null,
                  inventory_document_id uuid not null references inventory_documents(id) on delete cascade,
                  inventory_document_line_id uuid not null references inventory_document_lines(id) on delete cascade,
                  item_id uuid not null references inventory_items(id) on delete cascade,
                  warehouse_id uuid not null references inventory_warehouses(id) on delete cascade,
                  uom_code text not null,
                  activated_quantity numeric(20, 6) not null,
                  activated_by_user_id char(7) not null,
                  activated_at timestamptz not null default now()
                );

                create unique index if not exists ux_receipt_inventory_activation_lines_company_receipt_line
                  on {ActivationLinesTableName} (company_id, receipt_id, receipt_line_number);

                create index if not exists ix_receipt_inventory_activation_lines_company_receipt
                  on {ActivationLinesTableName} (company_id, receipt_id, activated_at desc);

                create table if not exists {ActivationFailuresTableName} (
                  id uuid primary key default gen_random_uuid(),
                  company_id char(7) not null references companies(id) on delete cascade,
                  receipt_id uuid not null,
                  failure_message text not null,
                  recorded_by_user_id char(7) not null,
                  recorded_at timestamptz not null default now()
                );

                create unique index if not exists ux_receipt_inventory_activation_failures_company_receipt
                  on {ActivationFailuresTableName} (company_id, receipt_id);
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
            $"""
            select
              to_regclass('inventory_documents') is not null
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'document_number')
              and to_regclass('{ActivationLinesTableName}') is not null
              and to_regclass('{ActivationFailuresTableName}') is not null;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static string BuildActivationDocumentNumber(DateOnly receiptDate) =>
        $"PRA-{receiptDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static string BuildActivationMemo(ReceiptActivationRecord receipt) =>
        string.IsNullOrWhiteSpace(receipt.Memo)
            ? $"Quantity activation from receipt {receipt.DisplayNumber}"
            : $"Quantity activation from receipt {receipt.DisplayNumber}: {receipt.Memo.Trim()}";

    private static string BuildActivationLineMemo(string displayNumber, int lineNumber) =>
        $"Receipt activation {displayNumber} line {lineNumber}";

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

    private sealed record ReceiptActivationRecord(
        Guid ReceiptId,
        string DisplayNumber,
        string Status,
        Guid VendorId,
        Guid WarehouseId,
        DateOnly ReceiptDate,
        string? Memo,
        DateTimeOffset? PostedAt,
        IReadOnlyList<ReceiptActivationLineRecord> Lines);

    private sealed record ReceiptActivationLineRecord(
        int LineNumber,
        Guid ItemId,
        decimal Quantity,
        string UomCode);

    private sealed record ReceiptActivationItemRecord(
        Guid Id,
        string Name,
        string ItemKind,
        string? StockUomCode,
        string ManageInventoryMethod);

    private sealed record ReceiptActivationWarehouseRecord(
        Guid Id,
        string WarehouseCode,
        string Name);

    private sealed record ReceiptActivationRow(
        int ReceiptLineNumber,
        Guid InventoryDocumentId,
        Guid InventoryDocumentLineId,
        decimal ActivatedQuantity,
        DateTimeOffset ActivatedAt);
}
