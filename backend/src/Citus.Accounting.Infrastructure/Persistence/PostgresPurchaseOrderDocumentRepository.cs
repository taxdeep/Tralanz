using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresPurchaseOrderDocumentRepository : IPurchaseOrderDocumentRepository
{
    private const string PurchaseOrdersTableName = "purchase_orders";
    private const string PurchaseOrderLinesTableName = "purchase_order_lines";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresPurchaseOrderDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<PurchaseOrderDocument?> GetAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        await using var headerCommand = scope.CreateCommand(
            $"""
            select
              id,
              entity_number,
              purchase_order_number,
              status,
              vendor_id,
              order_date,
              expected_date,
              vendor_reference,
              memo,
              issued_at
            from {PurchaseOrdersTableName}
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """);
        headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        headerCommand.Parameters.AddWithValue("document_id", documentId);

        Guid id;
        string entityNumber;
        string purchaseOrderNumber;
        string status;
        Guid vendorId;
        DateOnly orderDate;
        DateOnly? expectedDate;
        string? vendorReference;
        string? memo;
        DateTimeOffset? issuedAt;

        await using (var reader = await headerCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            purchaseOrderNumber = reader.GetString(reader.GetOrdinal("purchase_order_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            vendorId = reader.GetGuid(reader.GetOrdinal("vendor_id"));
            orderDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("order_date"));
            expectedDate = reader.IsDBNull(reader.GetOrdinal("expected_date"))
                ? null
                : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("expected_date"));
            vendorReference = reader.IsDBNull(reader.GetOrdinal("vendor_reference"))
                ? null
                : reader.GetString(reader.GetOrdinal("vendor_reference"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
            issuedAt = reader.IsDBNull(reader.GetOrdinal("issued_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("issued_at"));
        }

        var lines = await LoadLinesAsync(scope, companyId.Value, documentId, cancellationToken);
        return new PurchaseOrderDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(purchaseOrderNumber),
            status,
            vendorId,
            orderDate,
            lines,
            expectedDate,
            vendorReference,
            memo,
            issuedAt);
    }

    public async Task<IReadOnlyList<PurchaseOrderDocumentListItem>> ListAsync(
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

        await using var command = scope.CreateCommand(
            $"""
            select
              po.id,
              po.entity_number,
              po.purchase_order_number,
              po.status,
              po.vendor_id,
              po.order_date,
              po.expected_date,
              po.vendor_reference,
              po.memo,
              po.created_at,
              po.updated_at,
              po.issued_at,
              count(line.id)::int as line_count,
              coalesce(sum(line.ordered_quantity), 0)::numeric(18,6) as total_ordered_quantity
            from {PurchaseOrdersTableName} po
            left join {PurchaseOrderLinesTableName} line
              on line.company_id = po.company_id
             and line.purchase_order_id = po.id
            where po.company_id = @company_id
            group by po.id
            order by po.order_date desc, po.created_at desc
            limit @take;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("take", effectiveTake);

        var items = new List<PurchaseOrderDocumentListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PurchaseOrderDocumentListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("purchase_order_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetGuid(reader.GetOrdinal("vendor_id")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("order_date")),
                reader.IsDBNull(reader.GetOrdinal("expected_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("expected_date")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetDecimal(reader.GetOrdinal("total_ordered_quantity")),
                reader.IsDBNull(reader.GetOrdinal("vendor_reference")) ? null : reader.GetString(reader.GetOrdinal("vendor_reference")),
                reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
                reader.IsDBNull(reader.GetOrdinal("issued_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("issued_at"))));
        }

        return items;
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        PurchaseOrderDraftSaveModel draft,
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

        if (draft.DocumentId is null)
        {
            var year = draft.OrderDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                $"entity-number:all:{year}",
                $"EN{year}",
                8,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(connection, transaction, year, cancellationToken),
                cancellationToken);

            displayNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                "purchase-order-display",
                "PO-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId.Value,
                    PurchaseOrdersTableName,
                    "purchase_order_number",
                    "^PO-[0-9]+$",
                    6,
                    cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                $"""
                insert into {PurchaseOrdersTableName} (
                  id,
                  company_id,
                  entity_number,
                  purchase_order_number,
                  vendor_id,
                  status,
                  order_date,
                  expected_date,
                  vendor_reference,
                  memo,
                  created_by_user_id,
                  created_at,
                  updated_by_user_id,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @purchase_order_number,
                  @vendor_id,
                  @status,
                  @order_date,
                  @expected_date,
                  @vendor_reference,
                  @memo,
                  @created_by_user_id,
                  now(),
                  @updated_by_user_id,
                  now()
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
                draft.CompanyId.Value,
                documentId,
                cancellationToken);

            if (!PurchaseOrderDocumentStatuses.CanEdit(currentStatus))
            {
                throw new InvalidOperationException("Only draft purchase orders can be modified.");
            }

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                $"""
                update {PurchaseOrdersTableName}
                set vendor_id = @vendor_id,
                    order_date = @order_date,
                    expected_date = @expected_date,
                    vendor_reference = @vendor_reference,
                    memo = @memo,
                    updated_by_user_id = @updated_by_user_id,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = @status;
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, displayNumber, includeIdentity: false);
            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The purchase order draft could not be updated. Only draft purchase orders can be modified.");
            }
        }

        await using (var deleteLineCommand = connection.CreateCommand())
        {
            deleteLineCommand.Transaction = transaction;
            deleteLineCommand.CommandText =
                $"""
                delete from {PurchaseOrderLinesTableName}
                where company_id = @company_id
                  and purchase_order_id = @purchase_order_id;
                """;
            deleteLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteLineCommand.Parameters.AddWithValue("purchase_order_id", documentId);
            await deleteLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in draft.Lines.OrderBy(static line => line.LineNumber))
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                $"""
                insert into {PurchaseOrderLinesTableName} (
                  id,
                  company_id,
                  purchase_order_id,
                  line_number,
                  item_id,
                  ordered_quantity,
                  uom_code,
                  description,
                  unit_cost,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @purchase_order_id,
                  @line_number,
                  @item_id,
                  @ordered_quantity,
                  @uom_code,
                  @description,
                  @unit_cost,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("purchase_order_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("item_id", line.ItemId);
            insertLineCommand.Parameters.AddWithValue("ordered_quantity", Round6(line.OrderedQuantity));
            insertLineCommand.Parameters.AddWithValue("uom_code", line.UomCode.Trim().ToUpperInvariant());
            insertLineCommand.Parameters.Add(new NpgsqlParameter<string?>("description", NpgsqlDbType.Text)
            {
                TypedValue = string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim()
            });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<decimal?>("unit_cost", NpgsqlDbType.Numeric)
            {
                TypedValue = line.UnitCost.HasValue ? Round6(line.UnitCost.Value) : null
            });
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Draft);
    }

    public async Task<SourceDocumentDraftSaveResult> IssueAsync(
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
            companyId.Value,
            documentId,
            cancellationToken);

        if (!PurchaseOrderDocumentStatuses.CanIssue(currentStatus))
        {
            throw new InvalidOperationException("Only draft purchase orders can be issued.");
        }

        await using var issueCommand = connection.CreateCommand();
        issueCommand.Transaction = transaction;
        issueCommand.CommandText =
            $"""
            update {PurchaseOrdersTableName}
            set status = @issued_status,
                issued_by_user_id = @issued_by_user_id,
                issued_at = now(),
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = @draft_status;
            """;
        issueCommand.Parameters.AddWithValue("document_id", documentId);
        issueCommand.Parameters.AddWithValue("company_id", companyId.Value);
        issueCommand.Parameters.AddWithValue("issued_by_user_id", userId.Value);
        issueCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        issueCommand.Parameters.AddWithValue("issued_status", PurchaseOrderDocumentStatuses.Issued);
        issueCommand.Parameters.AddWithValue("draft_status", PurchaseOrderDocumentStatuses.Draft);

        if (await issueCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only draft purchase orders can be issued.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Issued);
    }

    public async Task<PurchaseOrderThreeQuantitySummary?> GetThreeQuantitySummaryAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        var summaries = await GetThreeQuantitySummariesAsync(companyId, [purchaseOrderId], cancellationToken);
        return summaries.TryGetValue(purchaseOrderId, out var summary) ? summary : null;
    }

    public async Task<IReadOnlyDictionary<Guid, PurchaseOrderThreeQuantitySummary>> GetThreeQuantitySummariesAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> purchaseOrderIds,
        CancellationToken cancellationToken)
    {
        if (purchaseOrderIds.Count == 0)
        {
            return new Dictionary<Guid, PurchaseOrderThreeQuantitySummary>();
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        var distinctIds = purchaseOrderIds.Where(static id => id != Guid.Empty).Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return new Dictionary<Guid, PurchaseOrderThreeQuantitySummary>();
        }

        var canReadReceiptAnchors =
            await TableExistsAsync(scope, "receipts", cancellationToken) &&
            await TableExistsAsync(scope, "receipt_lines", cancellationToken);
        var canReadBillAnchors =
            await TableExistsAsync(scope, "bills", cancellationToken) &&
            await TableExistsAsync(scope, "bill_lines", cancellationToken);

        var receiptAnchorJoin = canReadReceiptAnchors
            ? """
              left join lateral (
                select coalesce(sum(receipt_line.quantity), 0)::numeric(18,6) as received_quantity
                from receipt_lines receipt_line
                join receipts receipt
                  on receipt.company_id = receipt_line.company_id
                 and receipt.id = receipt_line.receipt_id
                where receipt_line.company_id = po_line.company_id
                  and receipt_line.purchase_order_id = po_line.purchase_order_id
                  and receipt_line.purchase_order_line_number = po_line.line_number
                  and receipt.status = 'posted'
              ) receipt on true
              """
            : """
              left join lateral (
                select 0::numeric(18,6) as received_quantity
              ) receipt on true
              """;
        var billAnchorJoin = canReadBillAnchors
            ? """
              left join lateral (
                select coalesce(sum(bill_line.quantity), 0)::numeric(18,6) as billed_quantity
                from bill_lines bill_line
                join bills bill
                  on bill.company_id = bill_line.company_id
                 and bill.id = bill_line.bill_id
                where bill_line.company_id = po_line.company_id
                  and bill_line.purchase_order_id = po_line.purchase_order_id
                  and bill_line.purchase_order_line_number = po_line.line_number
                  and bill.status = 'posted'
              ) bill on true
              """
            : """
              left join lateral (
                select 0::numeric(18,6) as billed_quantity
              ) bill on true
              """;

        await using var command = scope.CreateCommand(
            $"""
            with requested_purchase_orders as (
              select unnest(@purchase_order_ids::uuid[]) as purchase_order_id
            ),
            line_truth as (
              select
                po_line.purchase_order_id,
                po_line.line_number,
                po_line.item_id,
                po_line.uom_code,
                po_line.ordered_quantity,
                coalesce(receipt.received_quantity, 0)::numeric(18,6) as received_quantity,
                coalesce(bill.billed_quantity, 0)::numeric(18,6) as billed_quantity
              from {PurchaseOrderLinesTableName} po_line
              join requested_purchase_orders requested
                on requested.purchase_order_id = po_line.purchase_order_id
              {receiptAnchorJoin}
              {billAnchorJoin}
              where po_line.company_id = @company_id
            )
            select
              purchase_order_id,
              line_number,
              item_id,
              uom_code,
              ordered_quantity,
              received_quantity,
              billed_quantity
            from line_truth
            order by purchase_order_id, line_number;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("purchase_order_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = distinctIds
        });

        var groupedLines = new Dictionary<Guid, List<PurchaseOrderLineThreeQuantitySummary>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var purchaseOrderId = reader.GetGuid(reader.GetOrdinal("purchase_order_id"));
            var orderedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("ordered_quantity")));
            var receivedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("received_quantity")));
            var billedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("billed_quantity")));
            var lineSummary = new PurchaseOrderLineThreeQuantitySummary(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                orderedQuantity,
                receivedQuantity,
                billedQuantity,
                Math.Max(Round6(orderedQuantity - receivedQuantity), 0m),
                Math.Max(Round6(orderedQuantity - billedQuantity), 0m),
                PurchaseOrderThreeQuantityStatusPolicy.ResolveLineStatus(orderedQuantity, receivedQuantity, billedQuantity));

            if (!groupedLines.TryGetValue(purchaseOrderId, out var lines))
            {
                lines = [];
                groupedLines[purchaseOrderId] = lines;
            }

            lines.Add(lineSummary);
        }

        var summaries = new Dictionary<Guid, PurchaseOrderThreeQuantitySummary>();
        foreach (var (purchaseOrderId, lines) in groupedLines)
        {
            var orderedQuantity = Round6(lines.Sum(static line => line.OrderedQuantity));
            var receivedQuantity = Round6(lines.Sum(static line => line.ReceivedQuantity));
            var billedQuantity = Round6(lines.Sum(static line => line.BilledQuantity));
            var overReceivedLineCount = lines.Count(static line => line.QuantityStatus == PurchaseOrderThreeQuantityStatusPolicy.OverReceived);
            var overBilledLineCount = lines.Count(static line => line.QuantityStatus == PurchaseOrderThreeQuantityStatusPolicy.OverBilled);
            summaries[purchaseOrderId] = new PurchaseOrderThreeQuantitySummary(
                purchaseOrderId,
                lines.Count,
                orderedQuantity,
                receivedQuantity,
                billedQuantity,
                Math.Max(Round6(orderedQuantity - receivedQuantity), 0m),
                Math.Max(Round6(orderedQuantity - billedQuantity), 0m),
                overReceivedLineCount,
                overBilledLineCount,
                PurchaseOrderThreeQuantityStatusPolicy.ResolveSummaryStatus(
                    lines.Count,
                    overReceivedLineCount,
                    overBilledLineCount,
                    orderedQuantity,
                    receivedQuantity,
                    billedQuantity),
                lines);
        }

        return summaries;
    }

    private static async Task<IReadOnlyList<PurchaseOrderDocumentLine>> LoadLinesAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<PurchaseOrderDocumentLine>();
        await using var command = scope.CreateCommand(
            $"""
            select
              line_number,
              item_id,
              ordered_quantity,
              uom_code,
              description,
              unit_cost
            from {PurchaseOrderLinesTableName}
            where company_id = @company_id
              and purchase_order_id = @document_id
            order by line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new PurchaseOrderDocumentLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetDecimal(reader.GetOrdinal("ordered_quantity")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                reader.IsDBNull(reader.GetOrdinal("unit_cost")) ? null : reader.GetDecimal(reader.GetOrdinal("unit_cost"))));
        }

        return lines;
    }

    private static void ValidateDraft(PurchaseOrderDraftSaveModel draft)
    {
        if (draft.VendorId == Guid.Empty)
        {
            throw new InvalidOperationException("Purchase order draft requires a vendor.");
        }

        if (draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Purchase order draft must contain at least one line.");
        }

        var lineNumbers = new HashSet<int>();
        foreach (var line in draft.Lines)
        {
            if (!lineNumbers.Add(line.LineNumber))
            {
                throw new InvalidOperationException("Purchase order draft line numbers must be unique.");
            }

            _ = new PurchaseOrderDocumentLine(
                line.LineNumber,
                line.ItemId,
                line.OrderedQuantity,
                line.UomCode,
                line.Description,
                line.UnitCost);
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        PurchaseOrderDraftSaveModel draft,
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
            command.Parameters.AddWithValue("purchase_order_number", displayNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("updated_by_user_id", draft.UserId.Value);
        command.Parameters.AddWithValue("vendor_id", draft.VendorId);
        command.Parameters.AddWithValue("status", PurchaseOrderDocumentStatuses.Draft);
        command.Parameters.AddWithValue("order_date", draft.OrderDate);
        command.Parameters.Add(new NpgsqlParameter<DateOnly?>("expected_date", NpgsqlDbType.Date)
        {
            TypedValue = draft.ExpectedDate
        });
        command.Parameters.Add(new NpgsqlParameter<string?>("vendor_reference", NpgsqlDbType.Text)
        {
            TypedValue = string.IsNullOrWhiteSpace(draft.VendorReference) ? null : draft.VendorReference.Trim()
        });
        command.Parameters.Add(new NpgsqlParameter<string?>("memo", NpgsqlDbType.Text)
        {
            TypedValue = string.IsNullOrWhiteSpace(draft.Memo) ? null : draft.Memo.Trim()
        });
    }

    private static async Task<(string EntityNumber, string DisplayNumber, string Status)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select entity_number, purchase_order_number, status
            from {PurchaseOrdersTableName}
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Purchase order document was not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("purchase_order_number")),
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
            create table if not exists {PurchaseOrdersTableName} (
              id uuid primary key,
              company_id uuid not null,
              entity_number text not null,
              purchase_order_number text not null,
              vendor_id uuid not null,
              status text not null,
              order_date date not null,
              expected_date date null,
              vendor_reference text null,
              memo text null,
              created_by_user_id uuid not null,
              created_at timestamptz not null default now(),
              updated_by_user_id uuid null,
              updated_at timestamptz not null default now(),
              issued_by_user_id uuid null,
              issued_at timestamptz null
            );

            create unique index if not exists ux_purchase_orders_company_entity_number
              on {PurchaseOrdersTableName} (company_id, entity_number);

            create unique index if not exists ux_purchase_orders_company_purchase_order_number
              on {PurchaseOrdersTableName} (company_id, purchase_order_number);

            create index if not exists ix_purchase_orders_company_order_date
              on {PurchaseOrdersTableName} (company_id, order_date desc, created_at desc);

            create table if not exists {PurchaseOrderLinesTableName} (
              id uuid primary key,
              company_id uuid not null,
              purchase_order_id uuid not null,
              line_number integer not null,
              item_id uuid not null,
              ordered_quantity numeric(18,6) not null,
              uom_code text not null,
              description text null,
              unit_cost numeric(18,6) null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            );

            create unique index if not exists ux_purchase_order_lines_company_order_line
              on {PurchaseOrderLinesTableName} (company_id, purchase_order_id, line_number);

            create index if not exists ix_purchase_order_lines_company_order
              on {PurchaseOrderLinesTableName} (company_id, purchase_order_id, line_number);

            do $$
            begin
              if to_regclass('receipt_lines') is not null then
                alter table receipt_lines add column if not exists purchase_order_id uuid null;
                alter table receipt_lines add column if not exists purchase_order_line_number integer null;
                create index if not exists ix_receipt_lines_company_purchase_order_line
                  on receipt_lines (company_id, purchase_order_id, purchase_order_line_number);
              end if;

              if to_regclass('bill_lines') is not null then
                alter table bill_lines add column if not exists purchase_order_id uuid null;
                alter table bill_lines add column if not exists purchase_order_line_number integer null;
                create index if not exists ix_bill_lines_company_purchase_order_line
                  on bill_lines (company_id, purchase_order_id, purchase_order_line_number);
              end if;
            end $$;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        PostgresCommandScope scope,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand("select to_regclass(@table_name) is not null;");
        command.Parameters.AddWithValue("table_name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}
