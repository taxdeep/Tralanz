using Citus.Accounting.Application;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresReceiptDocumentRepository : IReceiptDocumentRepository
{
    private const string ReceiptsTableName = "receipts";
    private const string ReceiptLinesTableName = "receipt_lines";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresReceiptDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<ReceiptDocument?> GetAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        Guid id;
        string entityNumber;
        string receiptNumber;
        string status;
        Guid vendorId;
        Guid warehouseId;
        DateOnly receiptDate;
        string? vendorReference;
        string? sourceReference;
        string? memo;
        DateTimeOffset? postedAt;

        await using (var headerCommand = scope.CreateCommand(
                         $"""
                         select
                           r.id,
                           r.entity_number,
                           r.receipt_number,
                           r.status,
                           r.vendor_id,
                           r.warehouse_id,
                           r.receipt_date,
                           r.vendor_reference,
                           r.source_reference,
                           r.memo,
                           r.posted_at
                         from {ReceiptsTableName} r
                         where r.company_id = @company_id
                           and r.id = @document_id
                         limit 1;
                         """))
        {
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            receiptNumber = reader.GetString(reader.GetOrdinal("receipt_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            vendorId = reader.GetGuid(reader.GetOrdinal("vendor_id"));
            warehouseId = reader.GetGuid(reader.GetOrdinal("warehouse_id"));
            receiptDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date"));
            vendorReference = reader.IsDBNull(reader.GetOrdinal("vendor_reference"))
                ? null
                : reader.GetString(reader.GetOrdinal("vendor_reference"));
            sourceReference = reader.IsDBNull(reader.GetOrdinal("source_reference"))
                ? null
                : reader.GetString(reader.GetOrdinal("source_reference"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
            postedAt = reader.IsDBNull(reader.GetOrdinal("posted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"));
        }

        var lines = new List<ReceiptDocumentLine>();

        await using (var lineCommand = scope.CreateCommand(
                         $"""
                         select
                           l.line_number,
                           l.item_id,
                           l.quantity,
                           l.uom_code,
                           l.tracking_capture_home,
                           l.purchase_order_id,
                           l.purchase_order_line_number
                         from {ReceiptLinesTableName} l
                         where l.company_id = @company_id
                           and l.receipt_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            lineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            lineCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ReceiptDocumentLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.GetDecimal(reader.GetOrdinal("quantity")),
                    reader.GetString(reader.GetOrdinal("uom_code")),
                    reader.IsDBNull(reader.GetOrdinal("tracking_capture_home"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("tracking_capture_home")),
                    reader.IsDBNull(reader.GetOrdinal("purchase_order_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("purchase_order_id")),
                    reader.IsDBNull(reader.GetOrdinal("purchase_order_line_number"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("purchase_order_line_number"))));
            }
        }

        return new ReceiptDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(receiptNumber),
            status,
            vendorId,
            warehouseId,
            receiptDate,
            lines,
            vendorReference,
            sourceReference,
            memo,
            postedAt);
    }

    public async Task<IReadOnlyList<ReceiptDocumentListItem>> ListAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken)
    {
        var effectiveTake = take <= 0 ? 50 : Math.Min(take, 200);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        var items = new List<ReceiptDocumentListItem>();

        await using var command = scope.CreateCommand(
            $"""
            select
              r.id,
              r.entity_number,
              r.receipt_number,
              r.status,
              r.vendor_id,
              r.warehouse_id,
              r.receipt_date,
              r.vendor_reference,
              r.source_reference,
              r.memo,
              r.created_at,
              r.updated_at,
              r.posted_at,
              count(l.id)::int as line_count,
              coalesce(sum(l.quantity), 0)::numeric(18,6) as total_quantity
            from {ReceiptsTableName} r
            left join {ReceiptLinesTableName} l
              on l.company_id = r.company_id
             and l.receipt_id = r.id
            where r.company_id = @company_id
            group by
              r.id,
              r.entity_number,
              r.receipt_number,
              r.status,
              r.vendor_id,
              r.warehouse_id,
              r.receipt_date,
              r.vendor_reference,
              r.source_reference,
              r.memo,
              r.created_at,
              r.updated_at,
              r.posted_at
            order by r.receipt_date desc, r.created_at desc
            limit @take;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("take", effectiveTake);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ReceiptDocumentListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("receipt_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetDecimal(reader.GetOrdinal("total_quantity")),
                reader.IsDBNull(reader.GetOrdinal("vendor_reference")) ? null : reader.GetString(reader.GetOrdinal("vendor_reference")),
                reader.IsDBNull(reader.GetOrdinal("source_reference")) ? null : reader.GetString(reader.GetOrdinal("source_reference")),
                reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))));
        }

        return items;
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        ReceiptDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string displayNumber;

        await ValidatePurchaseOrderReceiptAnchorsAsync(
            connection,
            transaction,
            draft.CompanyId,
            draft.VendorId,
            documentId,
            draft.Lines
                .Where(static line => line.PurchaseOrderId.HasValue)
                .GroupBy(static line => (
                    PurchaseOrderId: line.PurchaseOrderId!.Value,
                    PurchaseOrderLineNumber: line.PurchaseOrderLineNumber!.Value,
                    line.ItemId,
                    UomCode: line.UomCode.Trim().ToUpperInvariant()))
                .Select(static group => new PurchaseOrderReceiptAnchorCandidate(
                    group.Key.PurchaseOrderId,
                    group.Key.PurchaseOrderLineNumber,
                    group.Key.ItemId,
                    group.Key.UomCode,
                    group.Sum(static line => line.Quantity)))
                .ToArray(),
            cancellationToken);

        if (draft.DocumentId is null)
        {
            var year = draft.ReceiptDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                8,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(connection, transaction, year, cancellationToken),
                cancellationToken);

            displayNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "receipt-display",
                "RECEIPT-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId,
                    ReceiptsTableName,
                    "receipt_number",
                    "^RECEIPT-[0-9]+$",
                    6,
                    cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                $"""
                insert into {ReceiptsTableName} (
                  id,
                  company_id,
                  entity_number,
                  receipt_number,
                  vendor_id,
                  warehouse_id,
                  status,
                  receipt_date,
                  vendor_reference,
                  source_reference,
                  memo,
                  created_by_user_id,
                  created_at,
                  updated_by_user_id,
                  updated_at,
                  posted_by_user_id,
                  posted_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @receipt_number,
                  @vendor_id,
                  @warehouse_id,
                  @status,
                  @receipt_date,
                  @vendor_reference,
                  @source_reference,
                  @memo,
                  @created_by_user_id,
                  now(),
                  @updated_by_user_id,
                  now(),
                  null,
                  null
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, displayNumber, includeIdentity: true);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, displayNumber, var currentStatus) = await LoadIdentityAsync(
                connection,
                transaction,
                draft.CompanyId,
                documentId,
                cancellationToken);

            if (!ReceiptDocumentStatuses.CanEdit(currentStatus))
            {
                throw new InvalidOperationException("Only draft receipts can be modified.");
            }

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                $"""
                update {ReceiptsTableName}
                set vendor_id = @vendor_id,
                    warehouse_id = @warehouse_id,
                    receipt_date = @receipt_date,
                    vendor_reference = @vendor_reference,
                    source_reference = @source_reference,
                    memo = @memo,
                    updated_by_user_id = @updated_by_user_id,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = @status;
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, displayNumber, includeIdentity: false);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException("The receipt draft could not be updated. Only draft receipts can be modified.");
            }
        }

        await using (var deleteLineCommand = connection.CreateCommand())
        {
            deleteLineCommand.Transaction = transaction;
            deleteLineCommand.CommandText =
                $"""
                delete from {ReceiptLinesTableName}
                where company_id = @company_id
                  and receipt_id = @receipt_id;
                """;
            deleteLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteLineCommand.Parameters.AddWithValue("receipt_id", documentId);
            await deleteLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in draft.Lines.OrderBy(static line => line.LineNumber))
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                $"""
                insert into {ReceiptLinesTableName} (
                  id,
                  company_id,
                  receipt_id,
                  line_number,
                  item_id,
                  quantity,
                  uom_code,
                  tracking_capture_home,
                  purchase_order_id,
                  purchase_order_line_number,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @receipt_id,
                  @line_number,
                  @item_id,
                  @quantity,
                  @uom_code,
                  @tracking_capture_home,
                  @purchase_order_id,
                  @purchase_order_line_number,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("receipt_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("item_id", line.ItemId);
            insertLineCommand.Parameters.AddWithValue("quantity", Round6(line.Quantity));
            insertLineCommand.Parameters.AddWithValue("uom_code", line.UomCode.Trim().ToUpperInvariant());
            insertLineCommand.Parameters.Add(new NpgsqlParameter<string?>("tracking_capture_home", NpgsqlDbType.Text)
            {
                TypedValue = string.IsNullOrWhiteSpace(line.TrackingCaptureHome) ? null : line.TrackingCaptureHome.Trim()
            });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("purchase_order_id", NpgsqlDbType.Uuid)
            {
                TypedValue = line.PurchaseOrderId
            });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<int?>("purchase_order_line_number", NpgsqlDbType.Integer)
            {
                TypedValue = line.PurchaseOrderLineNumber
            });
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, ReceiptDocumentStatuses.Draft);
    }

    public async Task<SourceDocumentDraftSaveResult> PostAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var (entityNumber, displayNumber, currentStatus) = await LoadIdentityAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        if (!ReceiptDocumentStatuses.CanPost(currentStatus))
        {
            throw new InvalidOperationException("Only draft receipts can be marked as posted.");
        }

        await ValidatePersistedPurchaseOrderReceiptAnchorsAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        await using var postCommand = connection.CreateCommand();
        postCommand.Transaction = transaction;
        postCommand.CommandText =
            $"""
            update {ReceiptsTableName}
            set status = @posted_status,
                posted_by_user_id = @posted_by_user_id,
                posted_at = now(),
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = @draft_status;
            """;
        postCommand.Parameters.AddWithValue("document_id", documentId);
        postCommand.Parameters.AddWithValue("company_id", companyId.Value);
        postCommand.Parameters.AddWithValue("posted_by_user_id", userId.Value);
        postCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        postCommand.Parameters.AddWithValue("posted_status", ReceiptDocumentStatuses.Posted);
        postCommand.Parameters.AddWithValue("draft_status", ReceiptDocumentStatuses.Draft);

        var affectedRows = await postCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("Only draft receipts can be marked as posted.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, ReceiptDocumentStatuses.Posted);
    }

    private static void ValidateDraft(ReceiptDraftSaveModel draft)
    {
        if (draft.VendorId == Guid.Empty)
        {
            throw new InvalidOperationException("Receipt draft requires a vendor.");
        }

        if (draft.WarehouseId == Guid.Empty)
        {
            throw new InvalidOperationException("Receipt draft requires a warehouse.");
        }

        if (draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Receipt draft must contain at least one line.");
        }

        var lineNumbers = new HashSet<int>();
        foreach (var line in draft.Lines)
        {
            if (!lineNumbers.Add(line.LineNumber))
            {
                throw new InvalidOperationException("Receipt draft line numbers must be unique.");
            }

            _ = new ReceiptDocumentLine(
                line.LineNumber,
                line.ItemId,
                line.Quantity,
                line.UomCode,
                line.TrackingCaptureHome,
                line.PurchaseOrderId,
                line.PurchaseOrderLineNumber);
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        ReceiptDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string displayNumber,
        bool includeIdentity)
    {
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("receipt_number", displayNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("updated_by_user_id", draft.UserId.Value);
        command.Parameters.AddWithValue("vendor_id", draft.VendorId);
        command.Parameters.AddWithValue("warehouse_id", draft.WarehouseId);
        command.Parameters.AddWithValue("status", ReceiptDocumentStatuses.Draft);
        command.Parameters.AddWithValue("receipt_date", draft.ReceiptDate);
        command.Parameters.Add(new NpgsqlParameter<string?>("vendor_reference", NpgsqlDbType.Text)
        {
            TypedValue = string.IsNullOrWhiteSpace(draft.VendorReference) ? null : draft.VendorReference.Trim()
        });
        command.Parameters.Add(new NpgsqlParameter<string?>("source_reference", NpgsqlDbType.Text)
        {
            TypedValue = string.IsNullOrWhiteSpace(draft.SourceReference) ? null : draft.SourceReference.Trim()
        });
        command.Parameters.Add(new NpgsqlParameter<string?>("memo", NpgsqlDbType.Text)
        {
            TypedValue = string.IsNullOrWhiteSpace(draft.Memo) ? null : draft.Memo.Trim()
        });
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static async Task ValidatePurchaseOrderReceiptAnchorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid receiptVendorId,
        Guid receiptDocumentId,
        IReadOnlyCollection<PurchaseOrderReceiptAnchorCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        await EnsurePurchaseOrderAnchorTablesAvailableAsync(connection, transaction, cancellationToken);

        foreach (var candidate in candidates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                with posted_receipt_quantity as (
                  select coalesce(sum(line.quantity), 0)::numeric(18,6) as posted_received_quantity
                  from receipt_lines line
                  join receipts receipt
                    on receipt.company_id = line.company_id
                   and receipt.id = line.receipt_id
                  where line.company_id = @company_id
                    and line.receipt_id <> @receipt_id
                    and line.purchase_order_id = @purchase_order_id
                    and line.purchase_order_line_number = @purchase_order_line_number
                    and receipt.status = 'posted'
                )
                select
                  po.status,
                  po.vendor_id,
                  po_line.item_id,
                  po_line.uom_code,
                  po_line.ordered_quantity,
                  coalesce(posted.posted_received_quantity, 0)::numeric(18,6) as posted_received_quantity
                from purchase_orders po
                join purchase_order_lines po_line
                  on po_line.company_id = po.company_id
                 and po_line.purchase_order_id = po.id
                 and po_line.line_number = @purchase_order_line_number
                cross join posted_receipt_quantity posted
                where po.company_id = @company_id
                  and po.id = @purchase_order_id
                limit 1;
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("receipt_id", receiptDocumentId);
            command.Parameters.AddWithValue("purchase_order_id", candidate.PurchaseOrderId);
            command.Parameters.AddWithValue("purchase_order_line_number", candidate.PurchaseOrderLineNumber);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("PO-anchored receipt lines must reference an existing purchase order line in the active company context.");
            }

            PurchaseOrderAnchorPolicy.EnsureAllowsNewAnchor(reader.GetString(reader.GetOrdinal("status")));

            if (reader.GetGuid(reader.GetOrdinal("vendor_id")) != receiptVendorId)
            {
                throw new InvalidOperationException($"PO-anchored receipt line {candidate.PurchaseOrderLineNumber} must use the same vendor as the purchase order.");
            }

            if (reader.GetGuid(reader.GetOrdinal("item_id")) != candidate.ItemId)
            {
                throw new InvalidOperationException($"PO-anchored receipt line {candidate.PurchaseOrderLineNumber} must use the same item as the purchase order line.");
            }

            var poUom = reader.GetString(reader.GetOrdinal("uom_code")).Trim().ToUpperInvariant();
            if (!string.Equals(poUom, candidate.UomCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"PO-anchored receipt line {candidate.PurchaseOrderLineNumber} must use the same stock UOM as the purchase order line.");
            }

            var orderedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("ordered_quantity")));
            var postedReceivedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("posted_received_quantity")));
            if (Round6(postedReceivedQuantity + candidate.Quantity) > orderedQuantity)
            {
                throw new InvalidOperationException($"PO-anchored receipt quantity exceeds the ordered quantity for purchase order {candidate.PurchaseOrderId:D} line {candidate.PurchaseOrderLineNumber}.");
            }
        }
    }

    private static async Task ValidatePersistedPurchaseOrderReceiptAnchorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid receiptDocumentId,
        CancellationToken cancellationToken)
    {
        await using var loadCommand = connection.CreateCommand();
        loadCommand.Transaction = transaction;
        loadCommand.CommandText =
            $"""
            select
              receipt.vendor_id,
              line.purchase_order_id,
              line.purchase_order_line_number,
              line.item_id,
              line.uom_code,
              sum(line.quantity)::numeric(18,6) as quantity
            from {ReceiptLinesTableName} line
            join {ReceiptsTableName} receipt
              on receipt.company_id = line.company_id
             and receipt.id = line.receipt_id
            where line.company_id = @company_id
              and line.receipt_id = @receipt_id
              and line.purchase_order_id is not null
              and line.purchase_order_line_number is not null
            group by
              receipt.vendor_id,
              line.purchase_order_id,
              line.purchase_order_line_number,
              line.item_id,
              line.uom_code;
            """;
        loadCommand.Parameters.AddWithValue("company_id", companyId.Value);
        loadCommand.Parameters.AddWithValue("receipt_id", receiptDocumentId);

        var candidates = new List<PurchaseOrderReceiptAnchorCandidate>();
        Guid? vendorId = null;
        await using (var reader = await loadCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                vendorId ??= reader.GetGuid(reader.GetOrdinal("vendor_id"));
                candidates.Add(new PurchaseOrderReceiptAnchorCandidate(
                    reader.GetGuid(reader.GetOrdinal("purchase_order_id")),
                    reader.GetInt32(reader.GetOrdinal("purchase_order_line_number")),
                    reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.GetString(reader.GetOrdinal("uom_code")).Trim().ToUpperInvariant(),
                    reader.GetDecimal(reader.GetOrdinal("quantity"))));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        await ValidatePurchaseOrderReceiptAnchorsAsync(
            connection,
            transaction,
            companyId,
            vendorId!.Value,
            receiptDocumentId,
            candidates,
            cancellationToken);
    }

    private static async Task EnsurePurchaseOrderAnchorTablesAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select to_regclass('purchase_orders') is not null and to_regclass('purchase_order_lines') is not null;";
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("PO anchors require first-class purchase order tables to exist.");
        }
    }

    private static async Task<(string EntityNumber, string DisplayNumber, string Status)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select entity_number, receipt_number, status
            from {ReceiptsTableName}
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Receipt document was not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("receipt_number")),
            reader.GetString(reader.GetOrdinal("status")));
    }

    private static async Task EnsureSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            create table if not exists {ReceiptsTableName} (
              id uuid primary key,
              company_id char(7) not null,
              entity_number char(11) not null,
              receipt_number text not null,
              vendor_id uuid not null,
              warehouse_id uuid not null,
              status text not null,
              receipt_date date not null,
              vendor_reference text null,
              source_reference text null,
              memo text null,
              created_by_user_id char(7) not null,
              created_at timestamptz not null default now(),
              updated_by_user_id char(7) null,
              updated_at timestamptz not null default now(),
              posted_by_user_id char(7) null,
              posted_at timestamptz null
            );

            create unique index if not exists ux_receipts_company_entity_number
              on {ReceiptsTableName} (company_id, entity_number);

            create unique index if not exists ux_receipts_company_receipt_number
              on {ReceiptsTableName} (company_id, receipt_number);

            create index if not exists ix_receipts_company_receipt_date
              on {ReceiptsTableName} (company_id, receipt_date desc, created_at desc);

            create table if not exists {ReceiptLinesTableName} (
              id uuid primary key,
              company_id char(7) not null,
              receipt_id uuid not null,
              line_number integer not null,
              item_id uuid not null,
              quantity numeric(18,6) not null,
              uom_code text not null,
              tracking_capture_home text null,
              purchase_order_id uuid null,
              purchase_order_line_number integer null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            alter table {ReceiptLinesTableName}
              add column if not exists purchase_order_id uuid null;

            alter table {ReceiptLinesTableName}
              add column if not exists purchase_order_line_number integer null;

            create unique index if not exists ux_receipt_lines_company_receipt_line
              on {ReceiptLinesTableName} (company_id, receipt_id, line_number);

            create index if not exists ix_receipt_lines_company_receipt
              on {ReceiptLinesTableName} (company_id, receipt_id, line_number);

            create index if not exists ix_receipt_lines_company_purchase_order_line
              on {ReceiptLinesTableName} (company_id, purchase_order_id, purchase_order_line_number);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PurchaseOrderReceiptAnchorCandidate(
        Guid PurchaseOrderId,
        int PurchaseOrderLineNumber,
        Guid ItemId,
        string UomCode,
        decimal Quantity);
}
