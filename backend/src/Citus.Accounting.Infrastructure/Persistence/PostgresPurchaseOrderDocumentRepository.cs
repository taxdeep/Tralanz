using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Application;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Text.Json;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresPurchaseOrderDocumentRepository : IPurchaseOrderDocumentRepository
{
    private const string PurchaseOrdersTableName = "purchase_orders";
    private const string PurchaseOrderLinesTableName = "purchase_order_lines";
    private const string QuantityDiscrepanciesTableName = "purchase_order_quantity_discrepancy_lanes";
    private const string PurchaseVarianceLinesTableName = "receipt_grir_ap_purchase_variance_lines";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
              approved_at,
              issued_at,
              closed_at,
              cancelled_at,
              amendment_started_at
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
        DateTimeOffset? approvedAt;
        DateTimeOffset? issuedAt;
        DateTimeOffset? closedAt;
        DateTimeOffset? cancelledAt;
        DateTimeOffset? amendmentStartedAt;

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
            approvedAt = reader.IsDBNull(reader.GetOrdinal("approved_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("approved_at"));
            issuedAt = reader.IsDBNull(reader.GetOrdinal("issued_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("issued_at"));
            closedAt = reader.IsDBNull(reader.GetOrdinal("closed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("closed_at"));
            cancelledAt = reader.IsDBNull(reader.GetOrdinal("cancelled_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("cancelled_at"));
            amendmentStartedAt = reader.IsDBNull(reader.GetOrdinal("amendment_started_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("amendment_started_at"));
        }

        var lines = await LoadLinesAsync(scope, companyId, documentId, cancellationToken);
        return new PurchaseOrderDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(purchaseOrderNumber),
            status,
            vendorId,
            orderDate,
            lines,
            expectedDate,
            vendorReference,
            memo,
            approvedAt,
            issuedAt,
            closedAt,
            cancelledAt,
            amendmentStartedAt);
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
              po.approved_at,
              po.issued_at,
              po.closed_at,
              po.cancelled_at,
              po.amendment_started_at,
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
                reader.IsDBNull(reader.GetOrdinal("approved_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("approved_at")),
                reader.IsDBNull(reader.GetOrdinal("issued_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("issued_at")),
                reader.IsDBNull(reader.GetOrdinal("closed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("closed_at")),
                reader.IsDBNull(reader.GetOrdinal("cancelled_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("cancelled_at")),
                reader.IsDBNull(reader.GetOrdinal("amendment_started_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("amendment_started_at"))));
        }

        return items;
    }

    public async Task<IReadOnlyList<PurchaseOrderLifecycleAuditEntry>> ListLifecycleAuditAsync(
        CompanyId companyId,
        Guid documentId,
        int take,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Purchase order document id is required.", nameof(documentId));
        }

        var effectiveTake = take <= 0 ? 50 : Math.Min(take, 200);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
        if (!await TableExistsAsync(scope, "audit_logs", cancellationToken))
        {
            return Array.Empty<PurchaseOrderLifecycleAuditEntry>();
        }

        await using var command = scope.CreateCommand(
            """
            select
              id,
              entity_id,
              action,
              actor_type,
              actor_id,
              payload ->> 'FromStatus' as from_status,
              payload ->> 'ToStatus' as to_status,
              payload ->> 'EntityNumber' as entity_number,
              payload ->> 'DisplayNumber' as display_number,
              created_at
            from audit_logs
            where company_id = @company_id
              and entity_type = 'purchase_order'
              and entity_id = @document_id
              and action = any(@actions::text[])
            order by created_at desc, id desc
            limit @take;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.Add(new NpgsqlParameter<string[]>("actions", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue =
            [
                "purchase_order_approved",
                "purchase_order_approval_reversed",
                "purchase_order_released",
                "purchase_order_reopened_for_amendment",
                "purchase_order_closed",
                "purchase_order_cancelled"
            ]
        });
        command.Parameters.AddWithValue("take", effectiveTake);

        var entries = new List<PurchaseOrderLifecycleAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new PurchaseOrderLifecycleAuditEntry(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("entity_id")),
                reader.GetString(reader.GetOrdinal("action")),
                reader.GetString(reader.GetOrdinal("actor_type")),
                reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
                reader.IsDBNull(reader.GetOrdinal("from_status")) ? null : reader.GetString(reader.GetOrdinal("from_status")),
                reader.IsDBNull(reader.GetOrdinal("to_status")) ? null : reader.GetString(reader.GetOrdinal("to_status")),
                reader.IsDBNull(reader.GetOrdinal("entity_number")) ? null : reader.GetString(reader.GetOrdinal("entity_number")),
                reader.IsDBNull(reader.GetOrdinal("display_number")) ? null : reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))));
        }

        return entries;
    }

    public async Task<PurchaseOrderApprovalRequestTransitionResult> RequestApprovalAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
        await EnsureApprovalAuditAvailableAsync(scope, cancellationToken);

        var (entityNumber, displayNumber, currentStatus) = await LoadIdentityAsync(
            scope.Connection,
            scope.Transaction,
            companyId,
            documentId,
            cancellationToken);

        if (!PurchaseOrderDocumentStatuses.CanApprove(currentStatus))
        {
            throw new InvalidOperationException("Only draft purchase orders can be submitted for approval.");
        }

        var existing = await GetLatestApprovalRequestAsync(scope, companyId, documentId, cancellationToken);
        if (existing is not null && existing.RequestStatus is "draft" or "submitted")
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                existing,
                "request",
                "already_requested",
                "A purchase order approval request is already open for this draft.");
        }

        var requestId = Guid.NewGuid();
        var estimatedAmount = await CalculateEstimatedAmountAsync(scope, companyId, documentId, cancellationToken);
        var thresholdAmount = PurchaseOrderApprovalThresholdPolicy.TemporaryGovernanceThresholdAmount;
        await AppendApprovalRequestAuditAsync(
            scope,
            companyId,
            requestId,
            userId,
            "purchase_order_approval_requested",
            new
            {
                RequestId = requestId,
                PurchaseOrderId = documentId,
                EntityNumber = entityNumber,
                DisplayNumber = displayNumber,
                PurchaseOrderStatus = currentStatus,
                EstimatedAmount = estimatedAmount,
                ThresholdAmount = thresholdAmount,
                RequiresGovernanceApproval = PurchaseOrderApprovalThresholdPolicy.RequiresGovernanceApproval(estimatedAmount),
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                RequestStatus = "draft",
                ApprovalStatus = "pending"
            },
            cancellationToken);

        var request = await GetApprovalRequestAsync(scope, companyId, documentId, requestId, cancellationToken)
            ?? throw new InvalidOperationException("Purchase order approval request was recorded but could not be reloaded.");

        return new PurchaseOrderApprovalRequestTransitionResult(
            request,
            "request",
            "requested",
            "Purchase order approval request was recorded. Submit it to place the request into the approval queue.");
    }

    public async Task<PurchaseOrderApprovalRequestRecord?> GetLatestApprovalRequestAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
        if (!await TableExistsAsync(scope, "audit_logs", cancellationToken))
        {
            return null;
        }

        return await GetLatestApprovalRequestAsync(scope, companyId, documentId, cancellationToken);
    }

    public async Task<IReadOnlyList<PurchaseOrderApprovalRequestRecord>> ListApprovalRequestsAsync(
        CompanyId companyId,
        int take,
        bool includeClosed,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
        if (!await TableExistsAsync(scope, "audit_logs", cancellationToken))
        {
            return Array.Empty<PurchaseOrderApprovalRequestRecord>();
        }

        var requested = await ListApprovalRequestRequestedEventsAsync(
            scope,
            companyId,
            Math.Clamp(take, 1, 200),
            cancellationToken);
        var requests = new List<PurchaseOrderApprovalRequestRecord>();
        foreach (var request in requested)
        {
            var record = await BuildApprovalRequestRecordAsync(scope, companyId, request, cancellationToken);
            if (includeClosed || string.Equals(record.ApprovalStatus, "pending", StringComparison.Ordinal))
            {
                requests.Add(record);
            }
        }

        return requests;
    }

    public async Task<PurchaseOrderApprovalRequestTransitionResult?> SubmitApprovalRequestAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
        await EnsureApprovalAuditAvailableAsync(scope, cancellationToken);

        var request = await GetApprovalRequestAsync(scope, companyId, documentId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        if (!string.Equals(request.ApprovalStatus, "pending", StringComparison.Ordinal))
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                request,
                "submit",
                "blocked_by_approval_status",
                $"Only pending purchase order approval requests can be submitted. Current approval status is '{request.ApprovalStatus}'.");
        }

        if (!string.Equals(request.PurchaseOrderStatus, PurchaseOrderDocumentStatuses.Draft, StringComparison.Ordinal))
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                request,
                "submit",
                "blocked_by_purchase_order_status",
                $"Only draft purchase orders can submit approval requests. Current purchase order status is '{request.PurchaseOrderStatus}'.");
        }

        if (request.RequestStatus == "submitted")
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                request,
                "submit",
                "already_submitted",
                "Purchase order approval request is already in the queue.");
        }

        if (request.RequestStatus != "draft")
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                request,
                "submit",
                "blocked_by_request_status",
                $"Only draft purchase order approval requests can be submitted. Current status is '{request.RequestStatus}'.");
        }

        await AppendApprovalRequestAuditAsync(
            scope,
            companyId,
            requestId,
            userId,
            "purchase_order_approval_submitted",
            new
            {
                request.RequestId,
                request.PurchaseOrderId,
                TransitionCode = "submit",
                PreviousRequestStatus = request.RequestStatus,
                RequestStatus = "submitted",
                ApprovalStatus = request.ApprovalStatus
            },
            cancellationToken);

        var updated = await GetApprovalRequestAsync(scope, companyId, documentId, requestId, cancellationToken) ?? request;
        return new PurchaseOrderApprovalRequestTransitionResult(
            updated,
            "submit",
            "submitted",
            "Purchase order approval request is now queued for approval review.");
    }

    public async Task<PurchaseOrderApprovalRequestTransitionResult?> RejectApprovalRequestAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);
        await EnsureApprovalAuditAvailableAsync(scope, cancellationToken);

        var request = await GetApprovalRequestAsync(scope, companyId, documentId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        if (!string.Equals(request.ApprovalStatus, "pending", StringComparison.Ordinal))
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                request,
                "reject",
                "blocked_by_approval_status",
                $"Only pending purchase order approval requests can be rejected. Current approval status is '{request.ApprovalStatus}'.");
        }

        if (request.RequestStatus != "submitted")
        {
            return new PurchaseOrderApprovalRequestTransitionResult(
                request,
                "reject",
                "blocked_by_request_status",
                $"Only submitted purchase order approval requests can be rejected. Current status is '{request.RequestStatus}'.");
        }

        await AppendApprovalRequestAuditAsync(
            scope,
            companyId,
            requestId,
            userId,
            "purchase_order_approval_rejected",
            new
            {
                request.RequestId,
                request.PurchaseOrderId,
                TransitionCode = "reject",
                PreviousRequestStatus = request.RequestStatus,
                RequestStatus = "rejected",
                ApprovalStatus = "rejected"
            },
            cancellationToken);

        var updated = await GetApprovalRequestAsync(scope, companyId, documentId, requestId, cancellationToken) ?? request;
        return new PurchaseOrderApprovalRequestTransitionResult(
            updated,
            "reject",
            "rejected",
            "Purchase order approval request was rejected. PO ordered truth remains unchanged.");
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
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                5,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(connection, transaction, year, cancellationToken),
                cancellationToken);

            displayNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "purchase-order-display",
                "PO-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId,
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
                draft.CompanyId,
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

    public async Task<SourceDocumentDraftSaveResult> ApproveAsync(
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

        if (!PurchaseOrderDocumentStatuses.CanApprove(currentStatus))
        {
            throw new InvalidOperationException("Only draft purchase orders can be approved.");
        }

        await using var approveCommand = connection.CreateCommand();
        approveCommand.Transaction = transaction;
        approveCommand.CommandText =
            $"""
            update {PurchaseOrdersTableName}
            set status = @approved_status,
                approved_by_user_id = @approved_by_user_id,
                approved_at = now(),
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = @draft_status;
            """;
        approveCommand.Parameters.AddWithValue("document_id", documentId);
        approveCommand.Parameters.AddWithValue("company_id", companyId.Value);
        approveCommand.Parameters.AddWithValue("approved_by_user_id", userId.Value);
        approveCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        approveCommand.Parameters.AddWithValue("approved_status", PurchaseOrderDocumentStatuses.Approved);
        approveCommand.Parameters.AddWithValue("draft_status", PurchaseOrderDocumentStatuses.Draft);

        if (await approveCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only draft purchase orders can be approved.");
        }

        await InsertLifecycleAuditLogIfAvailableAsync(
            connection,
            transaction,
            companyId,
            documentId,
            userId,
            "purchase_order_approved",
            currentStatus,
            PurchaseOrderDocumentStatuses.Approved,
            entityNumber,
            displayNumber,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Approved);
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
            companyId,
            documentId,
            cancellationToken);

        if (!PurchaseOrderDocumentStatuses.CanIssue(currentStatus))
        {
            throw new InvalidOperationException("Only approved purchase orders can be issued.");
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
              and status = @approved_status;
            """;
        issueCommand.Parameters.AddWithValue("document_id", documentId);
        issueCommand.Parameters.AddWithValue("company_id", companyId.Value);
        issueCommand.Parameters.AddWithValue("issued_by_user_id", userId.Value);
        issueCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        issueCommand.Parameters.AddWithValue("issued_status", PurchaseOrderDocumentStatuses.Issued);
        issueCommand.Parameters.AddWithValue("approved_status", PurchaseOrderDocumentStatuses.Approved);

        if (await issueCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only approved purchase orders can be issued.");
        }

        await InsertLifecycleAuditLogIfAvailableAsync(
            connection,
            transaction,
            companyId,
            documentId,
            userId,
            "purchase_order_released",
            currentStatus,
            PurchaseOrderDocumentStatuses.Issued,
            entityNumber,
            displayNumber,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Issued);
    }

    public async Task<SourceDocumentDraftSaveResult> ReverseApprovalAsync(
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

        if (!string.Equals(PurchaseOrderDocumentStatuses.Normalize(currentStatus), PurchaseOrderDocumentStatuses.Approved, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only approved purchase orders can have approval reversed.");
        }

        await using var reverseCommand = connection.CreateCommand();
        reverseCommand.Transaction = transaction;
        reverseCommand.CommandText =
            $"""
            update {PurchaseOrdersTableName}
            set status = @draft_status,
                approved_by_user_id = null,
                approved_at = null,
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = @approved_status;
            """;
        reverseCommand.Parameters.AddWithValue("document_id", documentId);
        reverseCommand.Parameters.AddWithValue("company_id", companyId.Value);
        reverseCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        reverseCommand.Parameters.AddWithValue("draft_status", PurchaseOrderDocumentStatuses.Draft);
        reverseCommand.Parameters.AddWithValue("approved_status", PurchaseOrderDocumentStatuses.Approved);

        if (await reverseCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only approved purchase orders can have approval reversed.");
        }

        await InsertLifecycleAuditLogIfAvailableAsync(
            connection,
            transaction,
            companyId,
            documentId,
            userId,
            "purchase_order_approval_reversed",
            currentStatus,
            PurchaseOrderDocumentStatuses.Draft,
            entityNumber,
            displayNumber,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Draft);
    }

    public async Task<SourceDocumentDraftSaveResult> ReopenForAmendmentAsync(
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

        if (!PurchaseOrderDocumentStatuses.CanReopenForAmendment(currentStatus))
        {
            throw new InvalidOperationException("Only approved or issued purchase orders can be reopened for amendment.");
        }

        await EnsureCanReopenForAmendmentAsync(connection, transaction, companyId, documentId, cancellationToken);

        await using var reopenCommand = connection.CreateCommand();
        reopenCommand.Transaction = transaction;
        reopenCommand.CommandText =
            $"""
            update {PurchaseOrdersTableName}
            set status = @draft_status,
                approved_by_user_id = null,
                approved_at = null,
                issued_by_user_id = null,
                issued_at = null,
                amendment_started_by_user_id = @amendment_started_by_user_id,
                amendment_started_at = now(),
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status in (@approved_status, @issued_status);
            """;
        reopenCommand.Parameters.AddWithValue("document_id", documentId);
        reopenCommand.Parameters.AddWithValue("company_id", companyId.Value);
        reopenCommand.Parameters.AddWithValue("amendment_started_by_user_id", userId.Value);
        reopenCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        reopenCommand.Parameters.AddWithValue("draft_status", PurchaseOrderDocumentStatuses.Draft);
        reopenCommand.Parameters.AddWithValue("approved_status", PurchaseOrderDocumentStatuses.Approved);
        reopenCommand.Parameters.AddWithValue("issued_status", PurchaseOrderDocumentStatuses.Issued);

        if (await reopenCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only approved or issued purchase orders can be reopened for amendment.");
        }

        await InsertLifecycleAuditLogIfAvailableAsync(
            connection,
            transaction,
            companyId,
            documentId,
            userId,
            "purchase_order_reopened_for_amendment",
            currentStatus,
            PurchaseOrderDocumentStatuses.Draft,
            entityNumber,
            displayNumber,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Draft);
    }

    public async Task<SourceDocumentDraftSaveResult> CloseAsync(
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

        if (!PurchaseOrderDocumentStatuses.CanClose(currentStatus))
        {
            throw new InvalidOperationException("Only issued purchase orders can be closed.");
        }

        var summary = await GetThreeQuantitySummaryAsync(companyId, documentId, cancellationToken);
        EnsureCanClose(summary);

        await using var closeCommand = connection.CreateCommand();
        closeCommand.Transaction = transaction;
        closeCommand.CommandText =
            $"""
            update {PurchaseOrdersTableName}
            set status = @closed_status,
                closed_by_user_id = @closed_by_user_id,
                closed_at = now(),
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = @issued_status;
            """;
        closeCommand.Parameters.AddWithValue("document_id", documentId);
        closeCommand.Parameters.AddWithValue("company_id", companyId.Value);
        closeCommand.Parameters.AddWithValue("closed_by_user_id", userId.Value);
        closeCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        closeCommand.Parameters.AddWithValue("closed_status", PurchaseOrderDocumentStatuses.Closed);
        closeCommand.Parameters.AddWithValue("issued_status", PurchaseOrderDocumentStatuses.Issued);

        if (await closeCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only issued purchase orders can be closed.");
        }

        await InsertLifecycleAuditLogIfAvailableAsync(
            connection,
            transaction,
            companyId,
            documentId,
            userId,
            "purchase_order_closed",
            currentStatus,
            PurchaseOrderDocumentStatuses.Closed,
            entityNumber,
            displayNumber,
            cancellationToken,
            accountingBoundary: "lifecycle_close_only",
            accountingEffectStatus: "not_posted",
            accountingBoundaryNote: "PO close freezes the purchase-order lifecycle only; it does not post a journal entry, settle GR/IR, or recognize purchase price variance.");

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Closed);
    }

    public async Task<SourceDocumentDraftSaveResult> CancelAsync(
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

        if (!PurchaseOrderDocumentStatuses.CanCancel(currentStatus))
        {
            throw new InvalidOperationException("Only draft, approved, or untouched issued purchase orders can be cancelled.");
        }

        var summary = await GetThreeQuantitySummaryAsync(companyId, documentId, cancellationToken);
        EnsureCanCancel(summary);

        await using var cancelCommand = connection.CreateCommand();
        cancelCommand.Transaction = transaction;
        cancelCommand.CommandText =
            $"""
            update {PurchaseOrdersTableName}
            set status = @cancelled_status,
                cancelled_by_user_id = @cancelled_by_user_id,
                cancelled_at = now(),
                updated_by_user_id = @updated_by_user_id,
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status in (@draft_status, @approved_status, @issued_status);
            """;
        cancelCommand.Parameters.AddWithValue("document_id", documentId);
        cancelCommand.Parameters.AddWithValue("company_id", companyId.Value);
        cancelCommand.Parameters.AddWithValue("cancelled_by_user_id", userId.Value);
        cancelCommand.Parameters.AddWithValue("updated_by_user_id", userId.Value);
        cancelCommand.Parameters.AddWithValue("cancelled_status", PurchaseOrderDocumentStatuses.Cancelled);
        cancelCommand.Parameters.AddWithValue("draft_status", PurchaseOrderDocumentStatuses.Draft);
        cancelCommand.Parameters.AddWithValue("approved_status", PurchaseOrderDocumentStatuses.Approved);
        cancelCommand.Parameters.AddWithValue("issued_status", PurchaseOrderDocumentStatuses.Issued);

        if (await cancelCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Only draft, approved, or untouched issued purchase orders can be cancelled.");
        }

        await InsertLifecycleAuditLogIfAvailableAsync(
            connection,
            transaction,
            companyId,
            documentId,
            userId,
            "purchase_order_cancelled",
            currentStatus,
            PurchaseOrderDocumentStatuses.Cancelled,
            entityNumber,
            displayNumber,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, PurchaseOrderDocumentStatuses.Cancelled);
    }

    public async Task ValidateBillAnchorsForPostingAsync(
        CompanyId companyId,
        Guid billDocumentId,
        CancellationToken cancellationToken)
    {
        if (billDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Bill document id is required.", nameof(billDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        if (!await TableExistsAsync(scope, "bills", cancellationToken) ||
            !await TableExistsAsync(scope, "bill_lines", cancellationToken))
        {
            return;
        }

        await using (var anchorCheckCommand = scope.CreateCommand(
                         """
                         select exists (
                           select 1
                           from bill_lines
                           where company_id = @company_id
                             and bill_id = @bill_id
                             and purchase_order_id is not null
                             and purchase_order_line_number is not null
                         );
                         """))
        {
            anchorCheckCommand.Parameters.AddWithValue("company_id", companyId.Value);
            anchorCheckCommand.Parameters.AddWithValue("bill_id", billDocumentId);
            if (await anchorCheckCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return;
            }
        }

        if (!await TableExistsAsync(scope, "receipts", cancellationToken) ||
            !await TableExistsAsync(scope, "receipt_lines", cancellationToken))
        {
            throw new InvalidOperationException("PO-anchored bill posting requires posted receipt truth before AP posting can continue.");
        }

        await using var command = scope.CreateCommand(
            $"""
            with bill_anchor_lines as (
              select
                bill.vendor_id,
                line.purchase_order_id,
                line.purchase_order_line_number,
                line.item_id,
                line.uom_code,
                coalesce(sum(line.quantity), 0)::numeric(18,6) as posting_quantity
              from bill_lines line
              join bills bill
                on bill.company_id = line.company_id
               and bill.id = line.bill_id
              where line.company_id = @company_id
                and line.bill_id = @bill_id
                and line.purchase_order_id is not null
                and line.purchase_order_line_number is not null
              group by
                bill.vendor_id,
                line.purchase_order_id,
                line.purchase_order_line_number,
                line.item_id,
                line.uom_code
            ),
            posted_bill_quantity as (
              select
                line.purchase_order_id,
                line.purchase_order_line_number,
                coalesce(sum(line.quantity), 0)::numeric(18,6) as posted_billed_quantity
              from bill_lines line
              join bills bill
                on bill.company_id = line.company_id
               and bill.id = line.bill_id
              where line.company_id = @company_id
                and line.bill_id <> @bill_id
                and line.purchase_order_id is not null
                and line.purchase_order_line_number is not null
                and bill.status = 'posted'
              group by line.purchase_order_id, line.purchase_order_line_number
            ),
            posted_receipt_quantity as (
              select
                line.purchase_order_id,
                line.purchase_order_line_number,
                coalesce(sum(line.quantity), 0)::numeric(18,6) as posted_received_quantity
              from receipt_lines line
              join receipts receipt
                on receipt.company_id = line.company_id
               and receipt.id = line.receipt_id
              where line.company_id = @company_id
                and line.purchase_order_id is not null
                and line.purchase_order_line_number is not null
                and receipt.status = 'posted'
              group by line.purchase_order_id, line.purchase_order_line_number
            )
            select
              anchor.purchase_order_id,
              anchor.purchase_order_line_number,
              anchor.vendor_id as bill_vendor_id,
              anchor.item_id as bill_item_id,
              anchor.uom_code as bill_uom_code,
              anchor.posting_quantity,
              po.status as purchase_order_status,
              po.vendor_id as purchase_order_vendor_id,
              po_line.item_id as purchase_order_item_id,
              po_line.uom_code as purchase_order_uom_code,
              po_line.ordered_quantity,
              coalesce(posted_bill.posted_billed_quantity, 0)::numeric(18,6) as posted_billed_quantity,
              coalesce(posted_receipt.posted_received_quantity, 0)::numeric(18,6) as posted_received_quantity
            from bill_anchor_lines anchor
            left join {PurchaseOrdersTableName} po
              on po.company_id = @company_id
             and po.id = anchor.purchase_order_id
            left join {PurchaseOrderLinesTableName} po_line
              on po_line.company_id = @company_id
             and po_line.purchase_order_id = anchor.purchase_order_id
             and po_line.line_number = anchor.purchase_order_line_number
            left join posted_bill_quantity posted_bill
              on posted_bill.purchase_order_id = anchor.purchase_order_id
             and posted_bill.purchase_order_line_number = anchor.purchase_order_line_number
            left join posted_receipt_quantity posted_receipt
              on posted_receipt.purchase_order_id = anchor.purchase_order_id
             and posted_receipt.purchase_order_line_number = anchor.purchase_order_line_number;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_id", billDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(reader.GetOrdinal("purchase_order_status")) ||
                reader.IsDBNull(reader.GetOrdinal("purchase_order_item_id")))
            {
                throw new InvalidOperationException("PO-anchored bill lines must reference an existing purchase order line in the active company context.");
            }

            PurchaseOrderAnchorPolicy.EnsureAllowsNewAnchor(reader.GetString(reader.GetOrdinal("purchase_order_status")));

            var purchaseOrderId = reader.GetGuid(reader.GetOrdinal("purchase_order_id"));
            var lineNumber = reader.GetInt32(reader.GetOrdinal("purchase_order_line_number"));
            var billVendorId = reader.GetGuid(reader.GetOrdinal("bill_vendor_id"));
            var poVendorId = reader.GetGuid(reader.GetOrdinal("purchase_order_vendor_id"));
            if (billVendorId != poVendorId)
            {
                throw new InvalidOperationException($"PO-anchored bill line {lineNumber} must use the same vendor as the purchase order.");
            }

            var billItemId = reader.GetGuid(reader.GetOrdinal("bill_item_id"));
            var poItemId = reader.GetGuid(reader.GetOrdinal("purchase_order_item_id"));
            if (billItemId != poItemId)
            {
                throw new InvalidOperationException($"PO-anchored bill line {lineNumber} must use the same item as the purchase order line.");
            }

            var billUom = reader.GetString(reader.GetOrdinal("bill_uom_code")).Trim().ToUpperInvariant();
            var poUom = reader.GetString(reader.GetOrdinal("purchase_order_uom_code")).Trim().ToUpperInvariant();
            if (!string.Equals(billUom, poUom, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"PO-anchored bill line {lineNumber} must use the same stock UOM as the purchase order line.");
            }

            var postingQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("posting_quantity")));
            var orderedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("ordered_quantity")));
            var postedBilledQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("posted_billed_quantity")));
            var postedReceivedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("posted_received_quantity")));
            var totalBilledQuantity = Round6(postedBilledQuantity + postingQuantity);

            if (totalBilledQuantity > orderedQuantity)
            {
                throw new InvalidOperationException($"PO-anchored bill quantity exceeds the ordered quantity for purchase order {purchaseOrderId:D} line {lineNumber}.");
            }

            if (totalBilledQuantity > postedReceivedQuantity)
            {
                throw new InvalidOperationException($"PO-anchored bill quantity cannot outrun posted receipt truth for purchase order {purchaseOrderId:D} line {lineNumber}.");
            }
        }
    }

    public async Task<PurchaseOrderThreeQuantitySummary?> GetThreeQuantitySummaryAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        var summaries = await GetThreeQuantitySummariesAsync(companyId, [purchaseOrderId], cancellationToken);
        return summaries.TryGetValue(purchaseOrderId, out var summary) ? summary : null;
    }

    public async Task<PurchaseOrderPurchaseVarianceSummary> GetPurchaseVarianceSummaryAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        if (!await TableExistsAsync(scope, "receipt_lines", cancellationToken) ||
            !await TableExistsAsync(scope, PurchaseVarianceLinesTableName, cancellationToken))
        {
            return BuildEmptyPurchaseVarianceSummary(purchaseOrderId);
        }

        await using var command = scope.CreateCommand(
            $"""
            select
              count(*)::int as variance_line_count,
              -- candidate_* aliases retained for wire compatibility; the
              -- variance_status now reads "recognised in settlement" (M4).
              count(*) filter (where variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement}')::int as candidate_line_count,
              count(*) filter (where variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.NoVariance}')::int as no_variance_line_count,
              count(*) filter (where variance.variance_status like 'blocked_%' or variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.VarianceInconsistent}')::int as blocked_line_count,
              coalesce(sum(variance.variance_amount_base) filter (where variance.variance_status = '{ReceiptGrIrApPurchaseVarianceStatusPolicy.RecognizedInSettlement}'), 0)::numeric(20,6) as candidate_variance_amount_base,
              max(variance.refreshed_at) as last_refreshed_at
            from {PurchaseVarianceLinesTableName} variance
            join receipt_lines receipt_line
              on receipt_line.company_id = variance.company_id
             and receipt_line.receipt_id = variance.receipt_id
             and receipt_line.line_number = variance.receipt_line_number
            where variance.company_id = @company_id
              and receipt_line.purchase_order_id = @purchase_order_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return BuildEmptyPurchaseVarianceSummary(purchaseOrderId);
        }

        var lineCount = reader.GetInt32(reader.GetOrdinal("variance_line_count"));
        var candidateLineCount = reader.GetInt32(reader.GetOrdinal("candidate_line_count"));
        var noVarianceLineCount = reader.GetInt32(reader.GetOrdinal("no_variance_line_count"));
        var blockedLineCount = reader.GetInt32(reader.GetOrdinal("blocked_line_count"));
        var candidateVarianceAmountBase = Round6(reader.GetFieldValue<decimal>(reader.GetOrdinal("candidate_variance_amount_base")));
        var readinessStatus = PurchaseOrderPurchaseVariancePostingReadinessPolicy.ResolveStatus(
            lineCount,
            candidateLineCount,
            blockedLineCount);

        return new PurchaseOrderPurchaseVarianceSummary(
            purchaseOrderId,
            lineCount,
            candidateLineCount,
            noVarianceLineCount,
            blockedLineCount,
            ReceiptGrIrApPurchaseVarianceStatusPolicy.ResolveSummaryStatus(
                lineCount,
                candidateLineCount,
                noVarianceLineCount,
                blockedLineCount),
            candidateVarianceAmountBase,
            PurchaseOrderPurchaseVariancePostingReadinessPolicy.CanRequestPosting(readinessStatus),
            readinessStatus,
            PurchaseOrderPurchaseVariancePostingReadinessPolicy.BuildReason(
                readinessStatus,
                candidateLineCount,
                blockedLineCount,
                candidateVarianceAmountBase),
            reader.IsDBNull(reader.GetOrdinal("last_refreshed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_refreshed_at")));
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
            var billedAheadOfReceivedLineCount = lines.Count(static line => line.QuantityStatus == PurchaseOrderThreeQuantityStatusPolicy.BilledAheadOfReceived);
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
                billedAheadOfReceivedLineCount,
                0,
                PurchaseOrderThreeQuantityStatusPolicy.ResolveSummaryStatus(
                    lines.Count,
                    overReceivedLineCount,
                    overBilledLineCount,
                    billedAheadOfReceivedLineCount,
                    orderedQuantity,
                    receivedQuantity,
                    billedQuantity),
                lines,
                Array.Empty<PurchaseOrderQuantityDiscrepancySummary>());
        }

        var discrepancyRows = await LoadQuantityDiscrepanciesAsync(scope, companyId, distinctIds, cancellationToken);
        if (discrepancyRows.Count == 0)
        {
            return summaries;
        }

        foreach (var discrepancyGroup in discrepancyRows.GroupBy(static row => row.PurchaseOrderId))
        {
            if (!summaries.TryGetValue(discrepancyGroup.Key, out var summary))
            {
                continue;
            }

            var materialized = discrepancyGroup.ToArray();
            summaries[discrepancyGroup.Key] = summary with
            {
                OpenDiscrepancyCount = materialized.Count(static row => string.Equals(row.InvestigationStatus, PurchaseOrderQuantityDiscrepancyPolicy.Open, StringComparison.Ordinal)),
                Discrepancies = materialized
            };
        }

        return summaries;
    }

    public async Task<PurchaseOrderThreeQuantitySummary?> RefreshQuantityDiscrepanciesAsync(
        CompanyId companyId,
        UserId userId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        if (purchaseOrderId == Guid.Empty)
        {
            throw new ArgumentException("Purchase order document id is required.", nameof(purchaseOrderId));
        }

        var summary = await GetThreeQuantitySummaryAsync(companyId, purchaseOrderId, cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var candidates = summary.Lines
            .Select(line => (Line: line, DiscrepancyType: PurchaseOrderQuantityDiscrepancyPolicy.ResolveDiscrepancyType(line)))
            .Where(static tuple => tuple.DiscrepancyType is not null)
            .ToArray();

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        var existing = await LoadExistingQuantityDiscrepancyReviewStateAsync(
            scope,
            companyId,
            purchaseOrderId,
            cancellationToken);

        await using (var deleteCommand = scope.CreateCommand(
                         $"""
                         delete from {QuantityDiscrepanciesTableName}
                         where company_id = @company_id
                           and purchase_order_id = @purchase_order_id
                           and discrepancy_type in (@over_received, @over_billed, @billed_ahead);
                         """))
        {
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);
            deleteCommand.Parameters.AddWithValue("over_received", PurchaseOrderQuantityDiscrepancyPolicy.OverReceived);
            deleteCommand.Parameters.AddWithValue("over_billed", PurchaseOrderQuantityDiscrepancyPolicy.OverBilled);
            deleteCommand.Parameters.AddWithValue("billed_ahead", PurchaseOrderQuantityDiscrepancyPolicy.BilledAheadOfReceived);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (candidates.Length > 0)
        {
            await using var insertCommand = scope.CreateCommand(
                $"""
                insert into {QuantityDiscrepanciesTableName} (
                  id,
                  company_id,
                  purchase_order_id,
                  purchase_order_line_number,
                  discrepancy_type,
                  investigation_status,
                  item_id,
                  uom_code,
                  ordered_quantity,
                  received_quantity,
                  billed_quantity,
                  remaining_to_receive_quantity,
                  remaining_to_bill_quantity,
                  summary,
                  first_detected_at,
                  last_detected_at,
                  review_note,
                  reviewed_by_user_id,
                  reviewed_at,
                  refreshed_by_user_id
                )
                values (
                  @id,
                  @company_id,
                  @purchase_order_id,
                  @purchase_order_line_number,
                  @discrepancy_type,
                  @investigation_status,
                  @item_id,
                  @uom_code,
                  @ordered_quantity,
                  @received_quantity,
                  @billed_quantity,
                  @remaining_to_receive_quantity,
                  @remaining_to_bill_quantity,
                  @summary,
                  @first_detected_at,
                  now(),
                  @review_note,
                  @reviewed_by_user_id,
                  @reviewed_at,
                  @refreshed_by_user_id
                );
                """);
            insertCommand.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid));
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);
            insertCommand.Parameters.Add(new NpgsqlParameter<int>("purchase_order_line_number", NpgsqlDbType.Integer));
            insertCommand.Parameters.Add(new NpgsqlParameter<string>("discrepancy_type", NpgsqlDbType.Text));
            insertCommand.Parameters.Add(new NpgsqlParameter<string>("investigation_status", NpgsqlDbType.Text));
            insertCommand.Parameters.Add(new NpgsqlParameter<Guid>("item_id", NpgsqlDbType.Uuid));
            insertCommand.Parameters.Add(new NpgsqlParameter<string>("uom_code", NpgsqlDbType.Text));
            insertCommand.Parameters.Add(new NpgsqlParameter<decimal>("ordered_quantity", NpgsqlDbType.Numeric));
            insertCommand.Parameters.Add(new NpgsqlParameter<decimal>("received_quantity", NpgsqlDbType.Numeric));
            insertCommand.Parameters.Add(new NpgsqlParameter<decimal>("billed_quantity", NpgsqlDbType.Numeric));
            insertCommand.Parameters.Add(new NpgsqlParameter<decimal>("remaining_to_receive_quantity", NpgsqlDbType.Numeric));
            insertCommand.Parameters.Add(new NpgsqlParameter<decimal>("remaining_to_bill_quantity", NpgsqlDbType.Numeric));
            insertCommand.Parameters.Add(new NpgsqlParameter<string>("summary", NpgsqlDbType.Text));
            insertCommand.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("first_detected_at", NpgsqlDbType.TimestampTz));
            insertCommand.Parameters.Add(new NpgsqlParameter<string?>("review_note", NpgsqlDbType.Text));
            insertCommand.Parameters.Add(new NpgsqlParameter<Guid?>("reviewed_by_user_id", NpgsqlDbType.Uuid));
            insertCommand.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("reviewed_at", NpgsqlDbType.TimestampTz));
            insertCommand.Parameters.AddWithValue("refreshed_by_user_id", userId.Value);

            foreach (var (line, discrepancyType) in candidates)
            {
                var key = (line.LineNumber, discrepancyType!);
                existing.TryGetValue(key, out var existingState);
                insertCommand.Parameters["id"].Value = Guid.NewGuid();
                insertCommand.Parameters["purchase_order_line_number"].Value = line.LineNumber;
                insertCommand.Parameters["discrepancy_type"].Value = discrepancyType!;
                insertCommand.Parameters["investigation_status"].Value = PurchaseOrderQuantityDiscrepancyPolicy.PreserveRefreshStatus(existingState?.InvestigationStatus);
                insertCommand.Parameters["item_id"].Value = line.ItemId;
                insertCommand.Parameters["uom_code"].Value = line.UomCode;
                insertCommand.Parameters["ordered_quantity"].Value = Round6(line.OrderedQuantity);
                insertCommand.Parameters["received_quantity"].Value = Round6(line.ReceivedQuantity);
                insertCommand.Parameters["billed_quantity"].Value = Round6(line.BilledQuantity);
                insertCommand.Parameters["remaining_to_receive_quantity"].Value = Round6(line.RemainingToReceiveQuantity);
                insertCommand.Parameters["remaining_to_bill_quantity"].Value = Round6(line.RemainingToBillQuantity);
                insertCommand.Parameters["summary"].Value = PurchaseOrderQuantityDiscrepancyPolicy.BuildDiscrepancySummary(
                    discrepancyType!,
                    line.OrderedQuantity,
                    line.ReceivedQuantity,
                    line.BilledQuantity,
                    line.UomCode);
                insertCommand.Parameters["first_detected_at"].Value = existingState is not null
                    ? existingState.FirstDetectedAt
                    : DateTimeOffset.UtcNow;
                insertCommand.Parameters["review_note"].Value = existingState?.ReviewNote is null ? DBNull.Value : existingState.ReviewNote;
                insertCommand.Parameters["reviewed_by_user_id"].Value = existingState?.ReviewedByUserId is null ? DBNull.Value : existingState.ReviewedByUserId.Value;
                insertCommand.Parameters["reviewed_at"].Value = existingState?.ReviewedAt is null ? DBNull.Value : existingState.ReviewedAt.Value;

                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return await GetThreeQuantitySummaryAsync(companyId, purchaseOrderId, cancellationToken);
    }

    public async Task<PurchaseOrderThreeQuantitySummary?> ReviewQuantityDiscrepancyAsync(
        CompanyId companyId,
        UserId userId,
        Guid purchaseOrderId,
        int purchaseOrderLineNumber,
        string discrepancyType,
        string investigationStatus,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
        if (purchaseOrderId == Guid.Empty)
        {
            throw new ArgumentException("Purchase order document id is required.", nameof(purchaseOrderId));
        }

        if (purchaseOrderLineNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(purchaseOrderLineNumber), "Purchase order line number must be positive.");
        }

        var normalizedStatus = PurchaseOrderQuantityDiscrepancyPolicy.NormalizeInvestigationStatus(investigationStatus);
        var normalizedDiscrepancyType = PurchaseOrderQuantityDiscrepancyPolicy.NormalizeDiscrepancyType(discrepancyType);

        if (normalizedStatus == PurchaseOrderQuantityDiscrepancyPolicy.Open)
        {
            throw new InvalidOperationException("Use refresh to reopen PO quantity discrepancies. Review actions can resolve or authorize override intent only.");
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await EnsureSchemaAsync(scope.Connection, scope.Transaction, cancellationToken);

        await using var command = scope.CreateCommand(
            $"""
            update {QuantityDiscrepanciesTableName}
            set investigation_status = @investigation_status,
                review_note = @review_note,
                reviewed_by_user_id = @reviewed_by_user_id,
                reviewed_at = now(),
                last_detected_at = now()
            where company_id = @company_id
              and purchase_order_id = @purchase_order_id
              and purchase_order_line_number = @purchase_order_line_number
              and discrepancy_type = @discrepancy_type;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);
        command.Parameters.AddWithValue("purchase_order_line_number", purchaseOrderLineNumber);
        command.Parameters.AddWithValue("discrepancy_type", normalizedDiscrepancyType);
        command.Parameters.AddWithValue("investigation_status", normalizedStatus);
        command.Parameters.Add(new NpgsqlParameter<string?>("review_note", NpgsqlDbType.Text)
        {
            TypedValue = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim()
        });
        command.Parameters.AddWithValue("reviewed_by_user_id", userId.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("PO quantity discrepancy lane was not found for review in the active company context. Refresh discrepancies before reviewing.");
        }

        return await GetThreeQuantitySummaryAsync(companyId, purchaseOrderId, cancellationToken);
    }

    private static async Task EnsureApprovalAuditAvailableAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(scope, "audit_logs", cancellationToken))
        {
            throw new InvalidOperationException("Purchase order approval request audit trail is not available.");
        }
    }

    private static async Task AppendApprovalRequestAuditAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid requestId,
        UserId actorId,
        string action,
        object payload,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            insert into audit_logs (
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload
            )
            values (
              @id,
              @company_id,
              'user',
              @actor_id,
              'purchase_order_approval_request',
              @entity_id,
              @action,
              @payload::jsonb
            );
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("entity_id", requestId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload, JsonOptions));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PurchaseOrderApprovalRequestRecord?> GetLatestApprovalRequestAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var requested = await LoadApprovalRequestRequestedEventAsync(
            scope,
            companyId,
            documentId,
            null,
            cancellationToken);

        return requested is null
            ? null
            : await BuildApprovalRequestRecordAsync(scope, companyId, requested, cancellationToken);
    }

    private static async Task<PurchaseOrderApprovalRequestRecord?> GetApprovalRequestAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var requested = await LoadApprovalRequestRequestedEventAsync(
            scope,
            companyId,
            documentId,
            requestId,
            cancellationToken);

        return requested is null
            ? null
            : await BuildApprovalRequestRecordAsync(scope, companyId, requested, cancellationToken);
    }

    private static async Task<ApprovalRequestRequestedEvent?> LoadApprovalRequestRequestedEventAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        Guid? requestId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              entity_id,
              actor_type,
              actor_id,
              coalesce(payload ->> 'purchaseOrderId', payload ->> 'PurchaseOrderId') as purchase_order_id,
              coalesce(payload ->> 'entityNumber', payload ->> 'EntityNumber') as entity_number,
              coalesce(payload ->> 'displayNumber', payload ->> 'DisplayNumber') as display_number,
              coalesce(payload ->> 'purchaseOrderStatus', payload ->> 'PurchaseOrderStatus') as purchase_order_status,
              coalesce(payload ->> 'estimatedAmount', payload ->> 'EstimatedAmount') as estimated_amount,
              coalesce(payload ->> 'thresholdAmount', payload ->> 'ThresholdAmount') as threshold_amount,
              coalesce(payload ->> 'requiresGovernanceApproval', payload ->> 'RequiresGovernanceApproval') as requires_governance_approval,
              coalesce(payload ->> 'requestStatus', payload ->> 'RequestStatus') as request_status,
              coalesce(payload ->> 'approvalStatus', payload ->> 'ApprovalStatus') as approval_status,
              coalesce(payload ->> 'reason', payload ->> 'Reason') as reason,
              created_at
            from audit_logs
            where company_id = @company_id
              and entity_type = 'purchase_order_approval_request'
              and action = 'purchase_order_approval_requested'
              and coalesce(payload ->> 'purchaseOrderId', payload ->> 'PurchaseOrderId') = @purchase_order_id
              and (@request_id is null or entity_id = @request_id)
            order by created_at desc, id desc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("purchase_order_id", documentId.ToString());
        command.Parameters.Add(new NpgsqlParameter<Guid?>("request_id", NpgsqlDbType.Uuid)
        {
            TypedValue = requestId
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadApprovalRequestRequestedEvent(reader);
    }

    private static async Task<IReadOnlyList<ApprovalRequestRequestedEvent>> ListApprovalRequestRequestedEventsAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              entity_id,
              actor_type,
              actor_id,
              coalesce(payload ->> 'purchaseOrderId', payload ->> 'PurchaseOrderId') as purchase_order_id,
              coalesce(payload ->> 'entityNumber', payload ->> 'EntityNumber') as entity_number,
              coalesce(payload ->> 'displayNumber', payload ->> 'DisplayNumber') as display_number,
              coalesce(payload ->> 'purchaseOrderStatus', payload ->> 'PurchaseOrderStatus') as purchase_order_status,
              coalesce(payload ->> 'estimatedAmount', payload ->> 'EstimatedAmount') as estimated_amount,
              coalesce(payload ->> 'thresholdAmount', payload ->> 'ThresholdAmount') as threshold_amount,
              coalesce(payload ->> 'requiresGovernanceApproval', payload ->> 'RequiresGovernanceApproval') as requires_governance_approval,
              coalesce(payload ->> 'requestStatus', payload ->> 'RequestStatus') as request_status,
              coalesce(payload ->> 'approvalStatus', payload ->> 'ApprovalStatus') as approval_status,
              coalesce(payload ->> 'reason', payload ->> 'Reason') as reason,
              created_at
            from audit_logs
            where company_id = @company_id
              and entity_type = 'purchase_order_approval_request'
              and action = 'purchase_order_approval_requested'
            order by created_at desc, id desc
            limit @take;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("take", Math.Clamp(take, 1, 200));

        var requests = new List<ApprovalRequestRequestedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(ReadApprovalRequestRequestedEvent(reader));
        }

        return requests;
    }

    private static async Task<PurchaseOrderApprovalRequestRecord> BuildApprovalRequestRecordAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        ApprovalRequestRequestedEvent requested,
        CancellationToken cancellationToken)
    {
        var submitted = await LoadApprovalRequestTransitionAsync(
            scope,
            companyId,
            requested.RequestId,
            "purchase_order_approval_submitted",
            cancellationToken);
        var rejected = await LoadApprovalRequestTransitionAsync(
            scope,
            companyId,
            requested.RequestId,
            "purchase_order_approval_rejected",
            cancellationToken);
        var currentPurchaseOrderStatus = await LoadPurchaseOrderStatusAsync(
            scope,
            companyId,
            requested.PurchaseOrderId,
            cancellationToken) ?? requested.PurchaseOrderStatus;

        var normalizedPurchaseOrderStatus = PurchaseOrderDocumentStatuses.Normalize(currentPurchaseOrderStatus);
        var approvalStatus = rejected is not null
            ? "rejected"
            : normalizedPurchaseOrderStatus is PurchaseOrderDocumentStatuses.Approved or PurchaseOrderDocumentStatuses.Issued or PurchaseOrderDocumentStatuses.Closed
                ? "approved"
                : normalizedPurchaseOrderStatus == PurchaseOrderDocumentStatuses.Cancelled
                    ? "cancelled"
                    : requested.ApprovalStatus;
        var requestStatus = rejected is not null
            ? "rejected"
            : submitted is not null
                ? "submitted"
                : requested.RequestStatus;

        return new PurchaseOrderApprovalRequestRecord(
            requested.RequestId,
            requested.PurchaseOrderId,
            companyId,
            requested.EntityNumber,
            requested.DisplayNumber,
            normalizedPurchaseOrderStatus,
            requested.EstimatedAmount,
            requested.ThresholdAmount,
            requested.RequiresGovernanceApproval,
            requestStatus,
            approvalStatus,
            requested.ActorType,
            requested.ActorId,
            requested.CreatedAt,
            submitted?.ActorType,
            submitted?.ActorId,
            submitted?.CreatedAt,
            rejected?.ActorType,
            rejected?.ActorId,
            rejected?.CreatedAt,
            requested.Reason);
    }

    private static async Task<ApprovalRequestTransitionEvent?> LoadApprovalRequestTransitionAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid requestId,
        string action,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              actor_type,
              actor_id,
              coalesce(payload ->> 'requestStatus', payload ->> 'RequestStatus') as request_status,
              coalesce(payload ->> 'approvalStatus', payload ->> 'ApprovalStatus') as approval_status,
              created_at
            from audit_logs
            where company_id = @company_id
              and entity_type = 'purchase_order_approval_request'
              and entity_id = @request_id
              and action = @action
            order by created_at desc, id desc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("action", action);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ApprovalRequestTransitionEvent(
            reader.GetString(reader.GetOrdinal("actor_type")),
            reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            reader.IsDBNull(reader.GetOrdinal("request_status")) ? null : reader.GetString(reader.GetOrdinal("request_status")),
            reader.IsDBNull(reader.GetOrdinal("approval_status")) ? null : reader.GetString(reader.GetOrdinal("approval_status")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")));
    }

    private static async Task<string?> LoadPurchaseOrderStatusAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select status
            from {PurchaseOrdersTableName}
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<decimal?> CalculateEstimatedAmountAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              bool_or(unit_cost is null) as has_missing_unit_cost,
              sum(ordered_quantity * unit_cost)::numeric(18,6) as estimated_amount
            from {PurchaseOrderLinesTableName}
            where company_id = @company_id
              and purchase_order_id = @document_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!reader.IsDBNull(reader.GetOrdinal("has_missing_unit_cost")) &&
            reader.GetBoolean(reader.GetOrdinal("has_missing_unit_cost")))
        {
            return null;
        }

        return reader.IsDBNull(reader.GetOrdinal("estimated_amount"))
            ? null
            : Round6(reader.GetDecimal(reader.GetOrdinal("estimated_amount")));
    }

    private static ApprovalRequestRequestedEvent ReadApprovalRequestRequestedEvent(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(reader.GetOrdinal("entity_id")),
            Guid.Parse(reader.GetString(reader.GetOrdinal("purchase_order_id"))),
            reader.IsDBNull(reader.GetOrdinal("entity_number")) ? string.Empty : reader.GetString(reader.GetOrdinal("entity_number")),
            reader.IsDBNull(reader.GetOrdinal("display_number")) ? string.Empty : reader.GetString(reader.GetOrdinal("display_number")),
            reader.IsDBNull(reader.GetOrdinal("purchase_order_status"))
                ? PurchaseOrderDocumentStatuses.Draft
                : reader.GetString(reader.GetOrdinal("purchase_order_status")),
            ReadNullableDecimal(reader, "estimated_amount"),
            ReadNullableDecimal(reader, "threshold_amount") ?? PurchaseOrderApprovalThresholdPolicy.TemporaryGovernanceThresholdAmount,
            ReadNullableBoolean(reader, "requires_governance_approval") ?? false,
            reader.IsDBNull(reader.GetOrdinal("request_status")) ? "draft" : reader.GetString(reader.GetOrdinal("request_status")),
            reader.IsDBNull(reader.GetOrdinal("approval_status")) ? "pending" : reader.GetString(reader.GetOrdinal("approval_status")),
            reader.GetString(reader.GetOrdinal("actor_type")),
            reader.IsDBNull(reader.GetOrdinal("actor_id")) ? null : (UserId?)UserId.Parse(reader.GetString(reader.GetOrdinal("actor_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("reason")) ? null : reader.GetString(reader.GetOrdinal("reason")));

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return decimal.TryParse(
            reader.GetString(ordinal),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static bool? ReadNullableBoolean(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return bool.TryParse(reader.GetString(ordinal), out var value)
            ? value
            : null;
    }

    private static async Task<IReadOnlyList<PurchaseOrderDocumentLine>> LoadLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
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

    private static void EnsureCanClose(PurchaseOrderThreeQuantitySummary? summary)
    {
        if (summary is null)
        {
            throw new InvalidOperationException("Purchase order three-quantity truth must be available before close.");
        }

        if (summary.Discrepancies.Count > 0)
        {
            throw new InvalidOperationException("Purchase orders with active quantity discrepancy lanes cannot be closed.");
        }

        if (!string.Equals(summary.QuantityStatus, PurchaseOrderThreeQuantityStatusPolicy.FullyBilled, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Purchase orders can close only after ordered, received, and billed quantities are fully aligned.");
        }

        if (summary.RemainingToReceiveQuantity != 0m || summary.RemainingToBillQuantity != 0m)
        {
            throw new InvalidOperationException("Purchase orders with remaining quantity cannot be closed.");
        }
    }

    private static void EnsureCanCancel(PurchaseOrderThreeQuantitySummary? summary)
    {
        if (summary is null)
        {
            throw new InvalidOperationException("Purchase order three-quantity truth must be available before cancellation.");
        }

        if (summary.Discrepancies.Count > 0)
        {
            throw new InvalidOperationException("Purchase orders with active quantity discrepancy lanes cannot be cancelled.");
        }

        if (summary.ReceivedQuantity != 0m || summary.BilledQuantity != 0m)
        {
            throw new InvalidOperationException("Purchase orders with posted receipt or bill truth cannot be cancelled.");
        }
    }

    private static async Task EnsureCanReopenForAmendmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (await HasPurchaseOrderAnchorsAsync(connection, transaction, "receipt_lines", companyId, documentId, cancellationToken))
        {
            throw new InvalidOperationException("Purchase orders with existing receipt anchors cannot be reopened for amendment.");
        }

        if (await HasPurchaseOrderAnchorsAsync(connection, transaction, "bill_lines", companyId, documentId, cancellationToken))
        {
            throw new InvalidOperationException("Purchase orders with existing bill anchors cannot be reopened for amendment.");
        }

        await using var discrepancyCommand = connection.CreateCommand();
        discrepancyCommand.Transaction = transaction;
        discrepancyCommand.CommandText =
            $"""
            select exists (
              select 1
              from {QuantityDiscrepanciesTableName}
              where company_id = @company_id
                and purchase_order_id = @document_id
                and investigation_status in ('open', 'override_authorized')
            );
            """;
        discrepancyCommand.Parameters.AddWithValue("company_id", companyId.Value);
        discrepancyCommand.Parameters.AddWithValue("document_id", documentId);

        if (await discrepancyCommand.ExecuteScalarAsync(cancellationToken) is true)
        {
            throw new InvalidOperationException("Purchase orders with active quantity discrepancy lanes cannot be reopened for amendment.");
        }
    }

    private static async Task<bool> HasPurchaseOrderAnchorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lineTableName,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, lineTableName, cancellationToken))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            select exists (
              select 1
              from {lineTableName}
              where company_id = @company_id
                and purchase_order_id = @document_id
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task InsertLifecycleAuditLogIfAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        UserId actorId,
        string action,
        string fromStatus,
        string toStatus,
        string entityNumber,
        string displayNumber,
        CancellationToken cancellationToken,
        string? accountingBoundary = null,
        string? accountingEffectStatus = null,
        string? accountingBoundaryNote = null)
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
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload
            )
            values (
              @id,
              @company_id,
              'user',
              @actor_id,
              'purchase_order',
              @entity_id,
              @action,
              @payload::jsonb
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("entity_id", documentId);
        command.Parameters.AddWithValue("action", action);
        var payload = new Dictionary<string, object?>
        {
            ["DocumentId"] = documentId,
            ["EntityNumber"] = entityNumber,
            ["DisplayNumber"] = displayNumber,
            ["FromStatus"] = PurchaseOrderDocumentStatuses.Normalize(fromStatus),
            ["ToStatus"] = PurchaseOrderDocumentStatuses.Normalize(toStatus),
            ["Action"] = action
        };

        if (!string.IsNullOrWhiteSpace(accountingBoundary))
        {
            payload["AccountingBoundary"] = accountingBoundary;
        }

        if (!string.IsNullOrWhiteSpace(accountingEffectStatus))
        {
            payload["AccountingEffectStatus"] = accountingEffectStatus;
        }

        if (!string.IsNullOrWhiteSpace(accountingBoundaryNote))
        {
            payload["AccountingBoundaryNote"] = accountingBoundaryNote;
        }

        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload, JsonOptions));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<PurchaseOrderQuantityDiscrepancySummary>> LoadQuantityDiscrepanciesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        IReadOnlyCollection<Guid> purchaseOrderIds,
        CancellationToken cancellationToken)
    {
        if (purchaseOrderIds.Count == 0)
        {
            return Array.Empty<PurchaseOrderQuantityDiscrepancySummary>();
        }

        await using var command = scope.CreateCommand(
            $"""
            select
              purchase_order_id,
              purchase_order_line_number,
              discrepancy_type,
              investigation_status,
              item_id,
              uom_code,
              ordered_quantity,
              received_quantity,
              billed_quantity,
              remaining_to_receive_quantity,
              remaining_to_bill_quantity,
              summary,
              first_detected_at,
              last_detected_at,
              review_note,
              reviewed_by_user_id,
              reviewed_at
            from {QuantityDiscrepanciesTableName}
            where company_id = @company_id
              and purchase_order_id = any(@purchase_order_ids::uuid[])
              and investigation_status in ('open', 'override_authorized')
            order by purchase_order_id, purchase_order_line_number, discrepancy_type;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("purchase_order_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = purchaseOrderIds.Where(static id => id != Guid.Empty).Distinct().ToArray()
        });

        var rows = new List<PurchaseOrderQuantityDiscrepancySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PurchaseOrderQuantityDiscrepancySummary(
                reader.GetGuid(reader.GetOrdinal("purchase_order_id")),
                reader.GetInt32(reader.GetOrdinal("purchase_order_line_number")),
                reader.GetString(reader.GetOrdinal("discrepancy_type")),
                reader.GetString(reader.GetOrdinal("investigation_status")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                Round6(reader.GetDecimal(reader.GetOrdinal("ordered_quantity"))),
                Round6(reader.GetDecimal(reader.GetOrdinal("received_quantity"))),
                Round6(reader.GetDecimal(reader.GetOrdinal("billed_quantity"))),
                Round6(reader.GetDecimal(reader.GetOrdinal("remaining_to_receive_quantity"))),
                Round6(reader.GetDecimal(reader.GetOrdinal("remaining_to_bill_quantity"))),
                reader.GetString(reader.GetOrdinal("summary")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("first_detected_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_detected_at")),
                reader.IsDBNull(reader.GetOrdinal("review_note")) ? null : reader.GetString(reader.GetOrdinal("review_note")),
                reader.IsDBNull(reader.GetOrdinal("reviewed_by_user_id")) ? null : UserId.Parse(reader.GetString(reader.GetOrdinal("reviewed_by_user_id"))),
                reader.IsDBNull(reader.GetOrdinal("reviewed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reviewed_at"))));
        }

        return rows;
    }

    private static async Task<Dictionary<(int LineNumber, string DiscrepancyType), ExistingQuantityDiscrepancyReviewState>> LoadExistingQuantityDiscrepancyReviewStateAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            select
              purchase_order_line_number,
              discrepancy_type,
              investigation_status,
              first_detected_at,
              review_note,
              reviewed_by_user_id,
              reviewed_at
            from {QuantityDiscrepanciesTableName}
            where company_id = @company_id
              and purchase_order_id = @purchase_order_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("purchase_order_id", purchaseOrderId);

        var rows = new Dictionary<(int LineNumber, string DiscrepancyType), ExistingQuantityDiscrepancyReviewState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[(reader.GetInt32(reader.GetOrdinal("purchase_order_line_number")), reader.GetString(reader.GetOrdinal("discrepancy_type")))] =
                new ExistingQuantityDiscrepancyReviewState(
                    reader.GetString(reader.GetOrdinal("investigation_status")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("first_detected_at")),
                    reader.IsDBNull(reader.GetOrdinal("review_note")) ? null : reader.GetString(reader.GetOrdinal("review_note")),
                    reader.IsDBNull(reader.GetOrdinal("reviewed_by_user_id")) ? null : UserId.Parse(reader.GetString(reader.GetOrdinal("reviewed_by_user_id"))),
                    reader.IsDBNull(reader.GetOrdinal("reviewed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reviewed_at")));
        }

        return rows;
    }

    private static PurchaseOrderPurchaseVarianceSummary BuildEmptyPurchaseVarianceSummary(Guid purchaseOrderId) =>
        new(
            purchaseOrderId,
            0,
            0,
            0,
            0,
            ReceiptGrIrApPurchaseVarianceStatusPolicy.NotApplicable,
            0m,
            false,
            PurchaseOrderPurchaseVariancePostingReadinessPolicy.NotApplicable,
            PurchaseOrderPurchaseVariancePostingReadinessPolicy.BuildReason(
                PurchaseOrderPurchaseVariancePostingReadinessPolicy.NotApplicable,
                0,
                0,
                0m),
            null);

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
        NpgsqlTransaction? transaction,
        CompanyId companyId,
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
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
              company_id char(7) not null,
              entity_number char(11) not null,
              purchase_order_number text not null,
              vendor_id uuid not null,
              status text not null,
              order_date date not null,
              expected_date date null,
              vendor_reference text null,
              memo text null,
              created_by_user_id char(7) not null,
              created_at timestamptz not null default now(),
              updated_by_user_id char(7) null,
              updated_at timestamptz not null default now(),
              approved_by_user_id char(7) null,
              approved_at timestamptz null,
              issued_by_user_id char(7) null,
              issued_at timestamptz null,
              closed_by_user_id char(7) null,
              closed_at timestamptz null,
              cancelled_by_user_id char(7) null,
              cancelled_at timestamptz null,
              amendment_started_by_user_id char(7) null,
              amendment_started_at timestamptz null
            );

            alter table {PurchaseOrdersTableName}
              add column if not exists approved_by_user_id char(7) null;

            alter table {PurchaseOrdersTableName}
              add column if not exists approved_at timestamptz null;

            alter table {PurchaseOrdersTableName}
              add column if not exists closed_by_user_id char(7) null;

            alter table {PurchaseOrdersTableName}
              add column if not exists closed_at timestamptz null;

            alter table {PurchaseOrdersTableName}
              add column if not exists cancelled_by_user_id char(7) null;

            alter table {PurchaseOrdersTableName}
              add column if not exists cancelled_at timestamptz null;

            alter table {PurchaseOrdersTableName}
              add column if not exists amendment_started_by_user_id char(7) null;

            alter table {PurchaseOrdersTableName}
              add column if not exists amendment_started_at timestamptz null;

            create unique index if not exists ux_purchase_orders_company_entity_number
              on {PurchaseOrdersTableName} (company_id, entity_number);

            create unique index if not exists ux_purchase_orders_company_purchase_order_number
              on {PurchaseOrdersTableName} (company_id, purchase_order_number);

            create index if not exists ix_purchase_orders_company_order_date
              on {PurchaseOrdersTableName} (company_id, order_date desc, created_at desc);

            create table if not exists {PurchaseOrderLinesTableName} (
              id uuid primary key,
              company_id char(7) not null,
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

            create table if not exists {QuantityDiscrepanciesTableName} (
              id uuid primary key,
              company_id char(7) not null,
              purchase_order_id uuid not null,
              purchase_order_line_number integer not null,
              discrepancy_type text not null,
              investigation_status text not null,
              item_id uuid not null,
              uom_code text not null,
              ordered_quantity numeric(18,6) not null,
              received_quantity numeric(18,6) not null,
              billed_quantity numeric(18,6) not null,
              remaining_to_receive_quantity numeric(18,6) not null,
              remaining_to_bill_quantity numeric(18,6) not null,
              summary text not null,
              first_detected_at timestamptz not null,
              last_detected_at timestamptz not null default now(),
              review_note text null,
              reviewed_by_user_id char(7) null,
              reviewed_at timestamptz null,
              refreshed_by_user_id char(7) not null
            );

            alter table {QuantityDiscrepanciesTableName}
              add column if not exists review_note text null;

            alter table {QuantityDiscrepanciesTableName}
              add column if not exists reviewed_by_user_id char(7) null;

            alter table {QuantityDiscrepanciesTableName}
              add column if not exists reviewed_at timestamptz null;

            alter table {QuantityDiscrepanciesTableName}
              drop constraint if exists ck_purchase_order_quantity_discrepancy_lanes_type;

            alter table {QuantityDiscrepanciesTableName}
              add constraint ck_purchase_order_quantity_discrepancy_lanes_type
                check (discrepancy_type in ('over_received', 'over_billed', 'billed_ahead_of_received'));

            alter table {QuantityDiscrepanciesTableName}
              drop constraint if exists ck_purchase_order_quantity_discrepancy_lanes_status;

            alter table {QuantityDiscrepanciesTableName}
              add constraint ck_purchase_order_quantity_discrepancy_lanes_status
                check (investigation_status in ('open', 'resolved', 'override_authorized'));

            create unique index if not exists ux_purchase_order_quantity_discrepancy_lanes_natural
              on {QuantityDiscrepanciesTableName} (company_id, purchase_order_id, purchase_order_line_number, discrepancy_type);

            create index if not exists ix_purchase_order_quantity_discrepancy_lanes_open
              on {QuantityDiscrepanciesTableName} (company_id, purchase_order_id, investigation_status);

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
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private sealed record ExistingQuantityDiscrepancyReviewState(
        string InvestigationStatus,
        DateTimeOffset FirstDetectedAt,
        string? ReviewNote,
        UserId? ReviewedByUserId,
        DateTimeOffset? ReviewedAt);

    private sealed record ApprovalRequestRequestedEvent(
        Guid RequestId,
        Guid PurchaseOrderId,
        string EntityNumber,
        string DisplayNumber,
        string PurchaseOrderStatus,
        decimal? EstimatedAmount,
        decimal ThresholdAmount,
        bool RequiresGovernanceApproval,
        string RequestStatus,
        string ApprovalStatus,
        string ActorType,
        UserId? ActorId,
        DateTimeOffset CreatedAt,
        string? Reason);

    private sealed record ApprovalRequestTransitionEvent(
        string ActorType,
        UserId? ActorId,
        string? RequestStatus,
        string? ApprovalStatus,
        DateTimeOffset CreatedAt);
}
