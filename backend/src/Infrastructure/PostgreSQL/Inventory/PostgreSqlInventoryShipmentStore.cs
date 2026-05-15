using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryShipmentStore : IInventoryShipmentStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryShipmentStore(
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

    public async Task<InventoryShipmentDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);

        var baseCurrencyCode = await LoadCompanyBaseCurrencyCodeAsync(connection, null, companyId, cancellationToken);
        var activeItems = await LoadActiveItemsAsync(connection, null, companyId, cancellationToken);
        var activeWarehouses = await LoadActiveWarehousesAsync(connection, null, companyId, cancellationToken);
        var recentShipments = await LoadRecentShipmentsAsync(connection, null, companyId, cancellationToken);

        return new InventoryShipmentDashboard(
            companyId,
            baseCurrencyCode,
            activeItems,
            activeWarehouses,
            recentShipments);
    }

    public async Task<InventoryShipmentSummary?> GetAsync(
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await RefreshShipmentIssueLaneAsync(connection, null, companyId, shipmentDocumentId, cancellationToken);

        await using var headerCommand = connection.CreateCommand();
        headerCommand.CommandText =
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
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.carrier_name,
              d.tracking_number,
              d.shipping_slip_number,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.id = @shipment_document_id
              and d.document_type = 'shipment'
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
              d.carrier_name,
              d.tracking_number,
              d.shipping_slip_number,
              d.memo
            limit 1;
            """;
        headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        headerCommand.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var documentId = reader.GetGuid(reader.GetOrdinal("id"));
        var summaryCompanyId = CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id")));
        var documentNumber = reader.GetString(reader.GetOrdinal("document_number"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        var postingDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date"));
        var customerId = reader.GetGuid(reader.GetOrdinal("customer_id"));
        var customerDisplayName = reader.GetString(reader.GetOrdinal("customer_display_name"));
        var totalQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero);
        var lineCount = reader.GetInt32(reader.GetOrdinal("line_count"));
        var createdAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"));
        DateTimeOffset? postedAt = reader.IsDBNull(reader.GetOrdinal("posted_at"))
            ? null
            : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"));
        var carrierName = reader.IsDBNull(reader.GetOrdinal("carrier_name"))
            ? null
            : reader.GetString(reader.GetOrdinal("carrier_name"));
        var trackingNumber = reader.IsDBNull(reader.GetOrdinal("tracking_number"))
            ? null
            : reader.GetString(reader.GetOrdinal("tracking_number"));
        var shippingSlipNumber = reader.IsDBNull(reader.GetOrdinal("shipping_slip_number"))
            ? null
            : reader.GetString(reader.GetOrdinal("shipping_slip_number"));
        var memo = reader.IsDBNull(reader.GetOrdinal("memo"))
            ? null
            : reader.GetString(reader.GetOrdinal("memo"));
        await reader.DisposeAsync();

        var issueCoverage = await LoadShipmentIssueCoverageAsync(connection, null, companyId, shipmentDocumentId, cancellationToken);
        var issueLineSummaries = await LoadShipmentIssueLineSummariesAsync(connection, null, companyId, shipmentDocumentId, cancellationToken);
        var recentIssues = await LoadShipmentAnchoredIssuesAsync(connection, null, companyId, shipmentDocumentId, cancellationToken);

        var summary = new InventoryShipmentSummary(
            documentId,
            summaryCompanyId,
            documentNumber,
            status,
            postingDate,
            customerId,
            customerDisplayName,
            totalQuantity,
            lineCount,
            createdAt,
            postedAt,
            carrierName,
            trackingNumber,
            shippingSlipNumber,
            memo,
            issueCoverage.IssuedQuantity,
            issueCoverage.RemainingQuantity,
            issueCoverage.MatchStatus,
            issueCoverage.LatestMatchedAt,
            issueLineSummaries,
            recentIssues,
            Array.Empty<InventoryShipmentLineInput>());

        var lines = await LoadShipmentLinesAsync(connection, null, companyId, shipmentDocumentId, cancellationToken);
        return summary with { Lines = lines };
    }

    public async Task<InventoryInvoiceShipmentHandoffSummary> GetInvoiceHandoffSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        var laneSummary = await GetInvoiceLaneSummaryAsync(companyId, invoiceDocumentId, cancellationToken);

        return new InventoryInvoiceShipmentHandoffSummary(
            laneSummary.InvoiceDocumentId,
            laneSummary.InvoiceOutboundLineCount,
            laneSummary.InvoiceOutboundQuantity,
            laneSummary.ShipmentCount,
            laneSummary.ShippedQuantity,
            laneSummary.RemainingToShipQuantity,
            laneSummary.ShipmentMatchStatus,
            laneSummary.LatestShipmentPostedAt,
            laneSummary.RecentShipments,
            laneSummary.LineSummaries.Select(static line => new InventoryInvoiceShipmentHandoffLineSummary(
                line.ItemId,
                line.ItemCode,
                line.ItemName,
                line.WarehouseId,
                line.WarehouseCode,
                line.WarehouseName,
                line.UomCode,
                line.InvoiceLineCount,
                line.InvoiceQuantity,
                line.ShippedQuantity,
                line.RemainingToShipQuantity,
                line.ShipmentMatchStatus)).ToArray());
    }

    public async Task<InventoryInvoiceShipmentIssueLaneSummary> GetInvoiceLaneSummaryAsync(
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await RefreshInvoiceShipmentLanesAsync(connection, null, companyId, new[] { invoiceDocumentId }, cancellationToken);
        await RefreshInvoiceShipmentIssueLanesAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);

        var invoiceCoverage = await LoadInvoiceShipmentCoverageAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);
        var invoiceState = await LoadInvoiceCoverageAsync(connection, null, companyId, invoiceDocumentId, invoiceCoverage, cancellationToken);
        var lineSummaries = await LoadInvoiceIssueLaneSummariesAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);
        var recentShipments = await LoadInvoiceAnchoredShipmentsAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);
        var recentIssues = await LoadInvoiceAnchoredIssuesAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);
        var issueCoverage = await LoadInvoiceIssueCoverageAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);
        var discrepancies = await LoadInvoiceOutboundDiscrepanciesAsync(connection, null, companyId, invoiceDocumentId, cancellationToken);

        return new InventoryInvoiceShipmentIssueLaneSummary(
            invoiceDocumentId,
            invoiceCoverage.InvoiceOutboundLineCount,
            invoiceCoverage.InvoiceOutboundQuantity,
            invoiceCoverage.ShipmentCount,
            invoiceCoverage.ShippedQuantity,
            invoiceCoverage.RemainingQuantity,
            invoiceCoverage.MatchStatus,
            invoiceState.InvoicedQuantity,
            invoiceState.RemainingToInvoiceQuantity,
            invoiceState.Status,
            issueCoverage.IssueCount,
            issueCoverage.IssuedQuantity,
            issueCoverage.RemainingQuantity,
            issueCoverage.MatchStatus,
            invoiceCoverage.LatestMatchedAt,
            invoiceState.InvoicePostedAt,
            issueCoverage.LatestMatchedAt,
            recentShipments,
            recentIssues,
            discrepancies,
            lineSummaries);
    }

    public async Task<IReadOnlyDictionary<Guid, InventoryInvoiceShipmentPostingGateSnapshot>> GetInvoicePostingGateSnapshotsAsync(
        CompanyId companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken)
    {
        if (invoiceDocumentIds.Count == 0)
        {
            return new Dictionary<Guid, InventoryInvoiceShipmentPostingGateSnapshot>();
        }

        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        var requestedInvoiceIds = invoiceDocumentIds.Distinct().ToArray();
        await RefreshInvoiceShipmentLanesAsync(connection, null, companyId, requestedInvoiceIds, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with requested_invoices as (
              select unnest(@invoice_document_ids::uuid[]) as invoice_document_id
            ),
            invoice_headers as (
              select
                i.id as invoice_document_id,
                i.status as invoice_status,
                i.posted_at as invoice_posted_at
              from invoices i
              where i.company_id = @company_id
                and i.id = any(@invoice_document_ids)
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
            lane_groups as (
              select
                source_document_id as invoice_document_id,
                coalesce(sum(matched_document_count), 0)::int as shipment_count,
                coalesce(sum(matched_quantity), 0) as shipped_quantity,
                max(latest_matched_at) as latest_shipment_posted_at
              from inventory_outbound_matching_lanes
              where company_id = @company_id
                and lane_type = 'invoice_shipment'
                and source_document_id = any(@invoice_document_ids)
              group by source_document_id
            )
            select
              ri.invoice_document_id,
              ih.invoice_status,
              ih.invoice_posted_at,
              coalesce(ig.invoice_outbound_line_count, 0) as invoice_outbound_line_count,
              coalesce(ig.invoice_outbound_quantity, 0) as invoice_outbound_quantity,
              coalesce(lg.shipment_count, 0) as shipment_count,
              coalesce(lg.shipped_quantity, 0) as shipped_quantity,
              lg.latest_shipment_posted_at
            from requested_invoices ri
            left join invoice_headers ih
              on ih.invoice_document_id = ri.invoice_document_id
            left join invoice_groups ig
              on ig.invoice_document_id = ri.invoice_document_id
            left join lane_groups lg
              on lg.invoice_document_id = ri.invoice_document_id;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("invoice_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = requestedInvoiceIds
        });
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var snapshots = new Dictionary<Guid, InventoryInvoiceShipmentPostingGateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceDocumentId = reader.GetGuid(reader.GetOrdinal("invoice_document_id"));
            var invoiceStatus = reader.IsDBNull(reader.GetOrdinal("invoice_status"))
                ? null
                : reader.GetString(reader.GetOrdinal("invoice_status"));
            var invoiceOutboundLineCount = reader.GetInt32(reader.GetOrdinal("invoice_outbound_line_count"));
            var invoiceOutboundQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("invoice_outbound_quantity")), 6, MidpointRounding.AwayFromZero);
            var shipmentCount = reader.GetInt32(reader.GetOrdinal("shipment_count"));
            var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(invoiceOutboundQuantity - shippedQuantity, 6, MidpointRounding.AwayFromZero);
            var invoicedQuantity = decimal.Round(ResolveInvoicedQuantity(invoiceStatus, invoiceOutboundQuantity), 6, MidpointRounding.AwayFromZero);
            var remainingToInvoiceQuantity = ResolveRemainingToInvoiceQuantity(invoiceStatus, shippedQuantity, invoiceOutboundQuantity);
            var matchStatus =
                invoiceOutboundLineCount == 0
                    ? "no_inventory_handoff"
                    : shipmentCount == 0
                        ? "no_shipment"
                        : remainingQuantity > 0m
                            ? "partially_shipped"
                            : remainingQuantity == 0m
                                ? "fully_shipped"
                                : "over_shipped";
            var invoiceCoverageStatus = ResolveInvoiceCoverageStatus(
                invoiceOutboundLineCount,
                invoiceStatus,
                shippedQuantity,
                invoiceOutboundQuantity);

            snapshots[invoiceDocumentId] = new InventoryInvoiceShipmentPostingGateSnapshot(
                invoiceDocumentId,
                invoiceOutboundLineCount,
                invoiceOutboundQuantity,
                shipmentCount,
                shippedQuantity,
                remainingQuantity,
                matchStatus,
                reader.IsDBNull(reader.GetOrdinal("latest_shipment_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_shipment_posted_at")),
                invoicedQuantity,
                remainingToInvoiceQuantity,
                invoiceCoverageStatus,
                reader.IsDBNull(reader.GetOrdinal("invoice_posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("invoice_posted_at")));
        }

        return snapshots;
    }

    public async Task<InventoryShipmentSummary> PostAsync(
        InventoryShipmentPostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken, allowCreate: false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
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
                    throw new InvalidOperationException("Each shipment line must reference an active inventory item in this company.");
                }

                if (item.ItemKind != InventoryItemKind.Stock)
                {
                    throw new InvalidOperationException($"Shipment only supports stock items. '{item.Name}' is not a stock item.");
                }

                if (item.ManageInventoryMethod != ManageInventoryMethod.ManageStock)
                {
                    throw new InvalidOperationException($"Shipment currently supports only warehouse-managed stock items. '{item.Name}' is not configured on that path.");
                }

                if (!warehouseMap.ContainsKey(line.WarehouseId))
                {
                    throw new InvalidOperationException("Each shipment line must reference an active warehouse in this company.");
                }

                if (!string.Equals(line.UomCode.Trim(), item.StockUomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Shipment line UOM must match the stock UOM for '{item.Name}'.");
                }
            }

            var documentId = Guid.NewGuid();
            var documentNumber = BuildShipmentNumber(request.PostingDate);
            var createdAt = DateTimeOffset.UtcNow;
            var totalQuantity = 0m;

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
                      carrier_name,
                      tracking_number,
                      shipping_slip_number,
                      memo,
                      created_by_user_id,
                      created_at,
                      posted_at
                    )
                    values (
                      @id,
                      @company_id,
                      @document_number,
                      'shipment',
                      'posted',
                      'neutral',
                      @posting_date,
                      @source_module,
                      @source_document_id,
                      @source_document_number,
                      @counterparty_id,
                      @carrier_name,
                      @tracking_number,
                      @shipping_slip_number,
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
                insertDocumentCommand.Parameters.AddWithValue("carrier_name", ToDbValue(request.CarrierName));
                insertDocumentCommand.Parameters.AddWithValue("tracking_number", ToDbValue(request.TrackingNumber));
                insertDocumentCommand.Parameters.AddWithValue("shipping_slip_number", ToDbValue(request.ShippingSlipNumber));
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", request.UserId.Value);
                insertDocumentCommand.Parameters.AddWithValue("created_at", createdAt);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", createdAt);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                var lineId = Guid.NewGuid();
                var baseQuantity = decimal.Round(line.Quantity, 6, MidpointRounding.AwayFromZero);
                totalQuantity += baseQuantity;

                await using var insertLineCommand = connection.CreateCommand();
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
                      null,
                      null,
                      null,
                      null,
                      null,
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
                insertLineCommand.Parameters.AddWithValue("uom_code", line.UomCode.Trim().ToUpperInvariant());
                insertLineCommand.Parameters.AddWithValue("quantity", baseQuantity);
                insertLineCommand.Parameters.AddWithValue("base_quantity", baseQuantity);
                insertLineCommand.Parameters.AddWithValue("reason_code", ToDbValue(line.ReasonCode));
                insertLineCommand.Parameters.AddWithValue("memo", ToDbValue(line.Memo));
                await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new InventoryShipmentSummary(
                documentId,
                request.CompanyId,
                documentNumber,
                "posted",
                request.PostingDate,
                request.CustomerId,
                customerDisplayName,
                decimal.Round(totalQuantity, 6, MidpointRounding.AwayFromZero),
                request.Lines.Count,
                createdAt,
                createdAt,
                NormalizeOptionalText(request.CarrierName),
                NormalizeOptionalText(request.TrackingNumber),
                NormalizeOptionalText(request.ShippingSlipNumber),
                NormalizeOptionalText(request.Memo),
                0m,
                decimal.Round(totalQuantity, 6, MidpointRounding.AwayFromZero),
                "pending_issue",
                null,
                Array.Empty<InventoryShipmentIssueLineSummary>(),
                Array.Empty<InventorySalesIssueSummary>(),
                request.Lines.OrderBy(line => line.LineNo).ToArray());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
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
                "Inventory shipment schema has not been installed. Apply database migrations before using inventory shipment features.");
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

                alter table inventory_documents
                  add column if not exists carrier_name text null;

                alter table inventory_documents
                  add column if not exists tracking_number text null;

                alter table inventory_documents
                  add column if not exists shipping_slip_number text null;

                create table if not exists inventory_outbound_matching_lanes (
                  id uuid primary key,
                  company_id char(7) not null,
                  lane_type text not null,
                  source_document_id uuid not null,
                  item_id uuid not null,
                  warehouse_id uuid not null,
                  uom_code text not null,
                  source_line_count integer not null,
                  source_quantity numeric(18,6) not null,
                  matched_document_count integer not null,
                  matched_quantity numeric(18,6) not null,
                  remaining_quantity numeric(18,6) not null,
                  status text not null,
                  latest_matched_at timestamptz null,
                  updated_at timestamptz not null
                );

                alter table inventory_outbound_matching_lanes
                  drop constraint if exists ck_inventory_outbound_matching_lanes_lane_type;

                alter table inventory_outbound_matching_lanes
                  add constraint ck_inventory_outbound_matching_lanes_lane_type
                    check (lane_type in ('invoice_shipment', 'shipment_issue'));

                create unique index if not exists ux_inventory_outbound_matching_lanes_natural
                  on inventory_outbound_matching_lanes (company_id, lane_type, source_document_id, item_id, warehouse_id, uom_code);

                create table if not exists inventory_outbound_discrepancy_lanes (
                  id uuid primary key,
                  company_id char(7) not null,
                  discrepancy_type text not null,
                  source_document_id uuid not null,
                  item_id uuid not null,
                  warehouse_id uuid not null,
                  uom_code text not null,
                  status text not null,
                  source_quantity numeric(18,6) not null,
                  matched_quantity numeric(18,6) not null,
                  remaining_quantity numeric(18,6) not null,
                  summary text not null,
                  latest_matched_at timestamptz null,
                  updated_at timestamptz not null
                );

                alter table inventory_outbound_discrepancy_lanes
                  drop constraint if exists ck_inventory_outbound_discrepancy_lanes_type;

                alter table inventory_outbound_discrepancy_lanes
                  add constraint ck_inventory_outbound_discrepancy_lanes_type
                    check (discrepancy_type in ('invoice_shipment', 'shipment_issue', 'invoice_coverage'));

                create unique index if not exists ux_inventory_outbound_discrepancy_lanes_natural
                  on inventory_outbound_discrepancy_lanes (company_id, discrepancy_type, source_document_id, item_id, warehouse_id, uom_code);

                alter table inventory_documents
                  drop constraint if exists ck_inventory_documents_document_type;

                alter table inventory_documents
                  add constraint ck_inventory_documents_document_type
                    check (document_type in (
                      'purchase_receipt',
                      'customer_return_receipt',
                      'transfer_receive',
                      'manufacturing_receipt',
                      'opening_balance_receipt',
                      'inventory_adjustment_gain',
                      'sales_issue',
                      'vendor_return_issue',
                      'transfer_ship',
                      'manufacturing_issue',
                      'inventory_write_off',
                      'inventory_adjustment_loss',
                      'shipment'
                    ));

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
              and to_regclass('inventory_outbound_matching_lanes') is not null
              and to_regclass('inventory_outbound_discrepancy_lanes') is not null
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
                  and column_name = 'carrier_name')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'tracking_number')
              and exists (
                select 1
                from information_schema.columns
                where table_schema = current_schema()
                  and table_name = 'inventory_documents'
                  and column_name = 'shipping_slip_number');
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
            where company_id = @company_id
              and id = @customer_id
              and is_active = true
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", customerId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string displayName || string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Shipment customer must be active in the current company context.");
        }

        return displayName.Trim();
    }

    private static async Task<IReadOnlyDictionary<Guid, InventoryManagedItemSummary>> LoadItemMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds.Length == 0)
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
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("item_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = itemIds
        });

        var items = new Dictionary<Guid, InventoryManagedItemSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(reader.GetOrdinal("id"));
            items[id] = new InventoryManagedItemSummary(
                id,
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
        }

        return items;
    }

    private static async Task<IReadOnlyDictionary<Guid, InventoryManagedWarehouseSummary>> LoadWarehouseMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] warehouseIds,
        CancellationToken cancellationToken)
    {
        if (warehouseIds.Length == 0)
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
              and id = any(@warehouse_ids)
              and is_active = true;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("warehouse_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = warehouseIds
        });

        var warehouses = new Dictionary<Guid, InventoryManagedWarehouseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(reader.GetOrdinal("id"));
            warehouses[id] = new InventoryManagedWarehouseSummary(
                id,
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")));
        }

        return warehouses;
    }

    private static async Task<IReadOnlyList<InventoryManagedItemSummary>> LoadActiveItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var ids = await LoadIdsAsync(connection, transaction, companyId, "inventory_items", cancellationToken);
        var map = await LoadItemMapAsync(connection, transaction, companyId, ids, cancellationToken);
        return map.Values.OrderBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<InventoryManagedWarehouseSummary>> LoadActiveWarehousesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var ids = await LoadIdsAsync(connection, transaction, companyId, "inventory_warehouses", cancellationToken);
        var map = await LoadWarehouseMapAsync(connection, transaction, companyId, ids, cancellationToken);
        return map.Values.OrderBy(warehouse => warehouse.WarehouseCode, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<Guid[]> LoadIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"select id from {tableName} where company_id = @company_id and is_active = true;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids.ToArray();
    }

    private static async Task<IReadOnlyList<InventoryShipmentLineInput>> LoadShipmentLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
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
        command.Parameters.AddWithValue("document_id", shipmentDocumentId);

        var lines = new List<InventoryShipmentLineInput>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new InventoryShipmentLineInput(
                reader.GetInt32(reader.GetOrdinal("line_no")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("base_quantity")), 6, MidpointRounding.AwayFromZero),
                reader.IsDBNull(reader.GetOrdinal("reason_code"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("reason_code")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo"))));
        }

        return lines;
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
            decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero));
    }

    private static async Task<IReadOnlyList<InventoryInvoiceShipmentHandoffLineSummary>> LoadInvoiceHandoffLineSummariesAsync(
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
            shipment_groups as (
                select
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code) as uom_code,
                  coalesce(sum(l.base_quantity), 0) as shipped_quantity
                from inventory_documents d
                join inventory_document_lines l
                  on l.document_id = d.id
                 and l.company_id = d.company_id
                where d.company_id = @company_id
                  and d.document_type = 'shipment'
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
              coalesce(s.shipped_quantity, 0) as shipped_quantity
            from invoice_groups i
            left join shipment_groups s
              on s.item_id = i.item_id
             and s.warehouse_id = i.warehouse_id
             and s.uom_code = i.uom_code
            order by i.item_code, i.warehouse_code, i.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var rows = new List<InventoryInvoiceShipmentHandoffLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("invoice_quantity")), 6, MidpointRounding.AwayFromZero);
            var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(invoiceQuantity - shippedQuantity, 6, MidpointRounding.AwayFromZero);
            var matchStatus =
                shippedQuantity <= 0m
                    ? "no_shipment"
                    : remainingQuantity > 0m
                        ? "partially_shipped"
                        : remainingQuantity == 0m
                            ? "fully_shipped"
                            : "over_shipped";

            rows.Add(new InventoryInvoiceShipmentHandoffLineSummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetInt32(reader.GetOrdinal("invoice_line_count")),
                invoiceQuantity,
                shippedQuantity,
                remainingQuantity,
                matchStatus));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<InventoryShipmentSummary>> LoadInvoiceAnchoredShipmentsAsync(
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
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.carrier_name,
              d.tracking_number,
              d.shipping_slip_number,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.document_type = 'shipment'
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
              d.carrier_name,
              d.tracking_number,
              d.shipping_slip_number,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var shipments = new List<InventoryShipmentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            shipments.Add(new InventoryShipmentSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_display_name")),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("carrier_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("carrier_name")),
                reader.IsDBNull(reader.GetOrdinal("tracking_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("tracking_number")),
                reader.IsDBNull(reader.GetOrdinal("shipping_slip_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("shipping_slip_number")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo")),
                0m,
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                "pending_issue",
                null,
                Array.Empty<InventoryShipmentIssueLineSummary>(),
                Array.Empty<InventorySalesIssueSummary>(),
                Array.Empty<InventoryShipmentLineInput>()));
        }

        return shipments;
    }

    private static async Task<IReadOnlyList<InventoryShipmentSummary>> LoadRecentShipmentsAsync(
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
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              d.carrier_name,
              d.tracking_number,
              d.shipping_slip_number,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.document_type = 'shipment'
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
              d.carrier_name,
              d.tracking_number,
              d.shipping_slip_number,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var shipments = new List<InventoryShipmentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            shipments.Add(new InventoryShipmentSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_display_name")),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("carrier_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("carrier_name")),
                reader.IsDBNull(reader.GetOrdinal("tracking_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("tracking_number")),
                reader.IsDBNull(reader.GetOrdinal("shipping_slip_number"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("shipping_slip_number")),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo")),
                0m,
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                "pending_issue",
                null,
                Array.Empty<InventoryShipmentIssueLineSummary>(),
                Array.Empty<InventorySalesIssueSummary>(),
                Array.Empty<InventoryShipmentLineInput>()));
        }

        return shipments;
    }

    private static async Task RefreshInvoiceShipmentLanesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        IReadOnlyCollection<Guid> invoiceDocumentIds,
        CancellationToken cancellationToken)
    {
        if (invoiceDocumentIds.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with invoice_groups as (
              select
                l.invoice_id as source_document_id,
                l.item_id,
                l.warehouse_id,
                upper(l.uom_code) as uom_code,
                count(*)::int as source_line_count,
                coalesce(sum(l.quantity), 0) as source_quantity
              from invoice_lines l
              where l.company_id = @company_id
                and l.invoice_id = any(@invoice_document_ids)
                and l.item_id is not null
                and l.warehouse_id is not null
                and l.uom_code is not null
              group by l.invoice_id, l.item_id, l.warehouse_id, upper(l.uom_code)
            ),
            shipment_groups as (
              select
                d.source_document_id,
                l.item_id,
                l.warehouse_id,
                upper(l.uom_code) as uom_code,
                count(distinct d.id)::int as matched_document_count,
                coalesce(sum(l.base_quantity), 0) as matched_quantity,
                max(d.posted_at) as latest_matched_at
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'shipment'
                and d.source_module = 'ar_invoice'
                and d.source_document_id = any(@invoice_document_ids)
              group by d.source_document_id, l.item_id, l.warehouse_id, upper(l.uom_code)
            ),
            combined as (
              select
                coalesce(i.source_document_id, s.source_document_id) as source_document_id,
                coalesce(i.item_id, s.item_id) as item_id,
                coalesce(i.warehouse_id, s.warehouse_id) as warehouse_id,
                coalesce(i.uom_code, s.uom_code) as uom_code,
                coalesce(i.source_line_count, 0) as source_line_count,
                coalesce(i.source_quantity, 0) as source_quantity,
                coalesce(s.matched_document_count, 0) as matched_document_count,
                coalesce(s.matched_quantity, 0) as matched_quantity,
                s.latest_matched_at
              from invoice_groups i
              full join shipment_groups s
                on s.source_document_id = i.source_document_id
               and s.item_id = i.item_id
               and s.warehouse_id = i.warehouse_id
               and s.uom_code = i.uom_code
            )
            select
              source_document_id,
              item_id,
              warehouse_id,
              uom_code,
              source_line_count,
              source_quantity,
              matched_document_count,
              matched_quantity,
              latest_matched_at
            from combined
            where source_document_id is not null;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("invoice_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = invoiceDocumentIds.Distinct().ToArray()
        });

        var rows = new List<(Guid SourceDocumentId, MatchingLaneRow Row)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceDocumentId = reader.GetGuid(reader.GetOrdinal("source_document_id"));
                var sourceQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("source_quantity")), 6, MidpointRounding.AwayFromZero);
                var matchedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("matched_quantity")), 6, MidpointRounding.AwayFromZero);
                rows.Add((sourceDocumentId, new MatchingLaneRow(
                    reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                    reader.GetString(reader.GetOrdinal("uom_code")),
                    reader.GetInt32(reader.GetOrdinal("source_line_count")),
                    sourceQuantity,
                    reader.GetInt32(reader.GetOrdinal("matched_document_count")),
                    matchedQuantity,
                    decimal.Round(sourceQuantity - matchedQuantity, 6, MidpointRounding.AwayFromZero),
                    ResolveInvoiceShipmentLineStatus(sourceQuantity, matchedQuantity),
                    reader.IsDBNull(reader.GetOrdinal("latest_matched_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_matched_at")))));
            }
        }

        await ReplaceMatchingLaneRowsAsync(
            connection,
            transaction,
            companyId,
            "invoice_shipment",
            invoiceDocumentIds.Distinct().ToArray(),
            rows,
            cancellationToken);

        var invoiceStatuses = await LoadInvoiceStatusMapAsync(connection, transaction, companyId, invoiceDocumentIds.Distinct().ToArray(), cancellationToken);
        var shipmentDiscrepancies = rows
            .Where(static tuple => string.Equals(tuple.Row.Status, "over_shipped", StringComparison.OrdinalIgnoreCase))
            .Select(static tuple => (tuple.SourceDocumentId, new DiscrepancyLaneRow(
                tuple.Row.ItemId,
                tuple.Row.WarehouseId,
                tuple.Row.UomCode,
                tuple.Row.Status,
                tuple.Row.SourceQuantity,
                tuple.Row.MatchedQuantity,
                tuple.Row.RemainingQuantity,
                tuple.Row.LatestMatchedAt,
                BuildDiscrepancySummary("invoice_shipment", tuple.Row.Status, tuple.Row.SourceQuantity, tuple.Row.MatchedQuantity, tuple.Row.RemainingQuantity))))
            .ToArray();
        var invoiceCoverageDiscrepancies = rows
            .Where(tuple =>
            {
                invoiceStatuses.TryGetValue(tuple.SourceDocumentId, out var invoiceStatus);
                return string.Equals(
                    ResolveInvoiceCoverageLineStatus(invoiceStatus, tuple.Row.MatchedQuantity, tuple.Row.SourceQuantity),
                    "over_invoiced",
                    StringComparison.OrdinalIgnoreCase);
            })
            .Select(tuple =>
            {
                invoiceStatuses.TryGetValue(tuple.SourceDocumentId, out var invoiceStatus);
                var status = ResolveInvoiceCoverageLineStatus(invoiceStatus, tuple.Row.MatchedQuantity, tuple.Row.SourceQuantity);
                var invoicedQuantity = ResolveInvoicedQuantity(invoiceStatus, tuple.Row.SourceQuantity);
                var remainingToInvoiceQuantity = ResolveRemainingToInvoiceQuantity(invoiceStatus, tuple.Row.MatchedQuantity, tuple.Row.SourceQuantity);
                var discrepancy = new DiscrepancyLaneRow(
                    tuple.Row.ItemId,
                    tuple.Row.WarehouseId,
                    tuple.Row.UomCode,
                    status,
                    tuple.Row.MatchedQuantity,
                    invoicedQuantity,
                    remainingToInvoiceQuantity,
                    tuple.Row.LatestMatchedAt,
                    BuildDiscrepancySummary("invoice_coverage", status, tuple.Row.MatchedQuantity, invoicedQuantity, remainingToInvoiceQuantity));
                return (tuple.SourceDocumentId, discrepancy);
            })
            .ToArray();

        await ReplaceDiscrepancyLaneRowsAsync(
            connection,
            transaction,
            companyId,
            "invoice_shipment",
            invoiceDocumentIds.Distinct().ToArray(),
            shipmentDiscrepancies,
            cancellationToken);
        await ReplaceDiscrepancyLaneRowsAsync(
            connection,
            transaction,
            companyId,
            "invoice_coverage",
            invoiceDocumentIds.Distinct().ToArray(),
            invoiceCoverageDiscrepancies,
            cancellationToken);
    }

    private static async Task RefreshInvoiceShipmentIssueLanesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        await using var shipmentCommand = connection.CreateCommand();
        shipmentCommand.Transaction = transaction;
        shipmentCommand.CommandText =
            """
            select id
            from inventory_documents
            where company_id = @company_id
              and document_type = 'shipment'
              and source_module = 'ar_invoice'
              and source_document_id = @invoice_document_id;
            """;
        shipmentCommand.Parameters.AddWithValue("company_id", companyId.Value);
        shipmentCommand.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var shipmentIds = new List<Guid>();
        await using (var reader = await shipmentCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                shipmentIds.Add(reader.GetGuid(reader.GetOrdinal("id")));
            }
        }

        foreach (var shipmentId in shipmentIds)
        {
            await RefreshShipmentIssueLaneAsync(connection, transaction, companyId, shipmentId, cancellationToken);
        }
    }

    private static async Task RefreshShipmentIssueLaneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with shipment_groups as (
              select
                l.document_id as source_document_id,
                l.item_id,
                l.warehouse_id,
                upper(l.uom_code) as uom_code,
                count(*)::int as source_line_count,
                coalesce(sum(l.base_quantity), 0) as source_quantity
              from inventory_document_lines l
              where l.company_id = @company_id
                and l.document_id = @shipment_document_id
              group by l.document_id, l.item_id, l.warehouse_id, upper(l.uom_code)
            ),
            issue_groups as (
              select
                d.source_document_id,
                l.item_id,
                l.warehouse_id,
                upper(l.uom_code) as uom_code,
                count(distinct d.id)::int as matched_document_count,
                coalesce(sum(l.base_quantity), 0) as matched_quantity,
                max(d.posted_at) as latest_matched_at
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'sales_issue'
                and d.source_module = 'inventory_shipment'
                and d.source_document_id = @shipment_document_id
              group by d.source_document_id, l.item_id, l.warehouse_id, upper(l.uom_code)
            ),
            combined as (
              select
                coalesce(s.source_document_id, i.source_document_id) as source_document_id,
                coalesce(s.item_id, i.item_id) as item_id,
                coalesce(s.warehouse_id, i.warehouse_id) as warehouse_id,
                coalesce(s.uom_code, i.uom_code) as uom_code,
                coalesce(s.source_line_count, 0) as source_line_count,
                coalesce(s.source_quantity, 0) as source_quantity,
                coalesce(i.matched_document_count, 0) as matched_document_count,
                coalesce(i.matched_quantity, 0) as matched_quantity,
                i.latest_matched_at
              from shipment_groups s
              full join issue_groups i
                on i.source_document_id = s.source_document_id
               and i.item_id = s.item_id
               and i.warehouse_id = s.warehouse_id
               and i.uom_code = s.uom_code
            )
            select
              source_document_id,
              item_id,
              warehouse_id,
              uom_code,
              source_line_count,
              source_quantity,
              matched_document_count,
              matched_quantity,
              latest_matched_at
            from combined
            where source_document_id is not null;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        var rows = new List<(Guid SourceDocumentId, MatchingLaneRow Row)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sourceDocumentId = reader.GetGuid(reader.GetOrdinal("source_document_id"));
                var sourceQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("source_quantity")), 6, MidpointRounding.AwayFromZero);
                var matchedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("matched_quantity")), 6, MidpointRounding.AwayFromZero);
                rows.Add((sourceDocumentId, new MatchingLaneRow(
                    reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                    reader.GetString(reader.GetOrdinal("uom_code")),
                    reader.GetInt32(reader.GetOrdinal("source_line_count")),
                    sourceQuantity,
                    reader.GetInt32(reader.GetOrdinal("matched_document_count")),
                    matchedQuantity,
                    decimal.Round(sourceQuantity - matchedQuantity, 6, MidpointRounding.AwayFromZero),
                    ResolveShipmentIssueLineStatus(sourceQuantity, matchedQuantity),
                    reader.IsDBNull(reader.GetOrdinal("latest_matched_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_matched_at")))));
            }
        }

        await ReplaceMatchingLaneRowsAsync(
            connection,
            transaction,
            companyId,
            "shipment_issue",
            new[] { shipmentDocumentId },
            rows,
            cancellationToken);

        var discrepancyRows = rows
            .Where(static tuple => string.Equals(tuple.Row.Status, "over_issued", StringComparison.OrdinalIgnoreCase))
            .Select(static tuple => (tuple.SourceDocumentId, new DiscrepancyLaneRow(
                tuple.Row.ItemId,
                tuple.Row.WarehouseId,
                tuple.Row.UomCode,
                tuple.Row.Status,
                tuple.Row.SourceQuantity,
                tuple.Row.MatchedQuantity,
                tuple.Row.RemainingQuantity,
                tuple.Row.LatestMatchedAt,
                BuildDiscrepancySummary("shipment_issue", tuple.Row.Status, tuple.Row.SourceQuantity, tuple.Row.MatchedQuantity, tuple.Row.RemainingQuantity))))
            .ToArray();

        await ReplaceDiscrepancyLaneRowsAsync(
            connection,
            transaction,
            companyId,
            "shipment_issue",
            new[] { shipmentDocumentId },
            discrepancyRows,
            cancellationToken);
    }

    private static async Task ReplaceMatchingLaneRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        string laneType,
        Guid[] sourceDocumentIds,
        IReadOnlyCollection<(Guid SourceDocumentId, MatchingLaneRow Row)> rows,
        CancellationToken cancellationToken)
    {
        if (sourceDocumentIds.Length == 0)
        {
            return;
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from inventory_outbound_matching_lanes
                where company_id = @company_id
                  and lane_type = @lane_type
                  and source_document_id = any(@source_document_ids);
                """;
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("lane_type", laneType);
            deleteCommand.Parameters.Add(new NpgsqlParameter<Guid[]>("source_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = sourceDocumentIds
            });
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (sourceDocumentId, row) in rows)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into inventory_outbound_matching_lanes (
                  id,
                  company_id,
                  lane_type,
                  source_document_id,
                  item_id,
                  warehouse_id,
                  uom_code,
                  source_line_count,
                  source_quantity,
                  matched_document_count,
                  matched_quantity,
                  remaining_quantity,
                  status,
                  latest_matched_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @lane_type,
                  @source_document_id,
                  @item_id,
                  @warehouse_id,
                  @uom_code,
                  @source_line_count,
                  @source_quantity,
                  @matched_document_count,
                  @matched_quantity,
                  @remaining_quantity,
                  @status,
                  @latest_matched_at,
                  @updated_at
                );
                """;
            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("lane_type", laneType);
            insertCommand.Parameters.AddWithValue("source_document_id", sourceDocumentId);
            insertCommand.Parameters.AddWithValue("item_id", row.ItemId);
            insertCommand.Parameters.AddWithValue("warehouse_id", row.WarehouseId);
            insertCommand.Parameters.AddWithValue("uom_code", row.UomCode);
            insertCommand.Parameters.AddWithValue("source_line_count", row.SourceLineCount);
            insertCommand.Parameters.AddWithValue("source_quantity", row.SourceQuantity);
            insertCommand.Parameters.AddWithValue("matched_document_count", row.MatchedDocumentCount);
            insertCommand.Parameters.AddWithValue("matched_quantity", row.MatchedQuantity);
            insertCommand.Parameters.AddWithValue("remaining_quantity", row.RemainingQuantity);
            insertCommand.Parameters.AddWithValue("status", row.Status);
            insertCommand.Parameters.AddWithValue("latest_matched_at", row.LatestMatchedAt ?? (object)DBNull.Value);
            insertCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReplaceDiscrepancyLaneRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        string discrepancyType,
        Guid[] sourceDocumentIds,
        IReadOnlyCollection<(Guid SourceDocumentId, DiscrepancyLaneRow Row)> rows,
        CancellationToken cancellationToken)
    {
        if (sourceDocumentIds.Length == 0)
        {
            return;
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from inventory_outbound_discrepancy_lanes
                where company_id = @company_id
                  and discrepancy_type = @discrepancy_type
                  and source_document_id = any(@source_document_ids);
                """;
            deleteCommand.Parameters.AddWithValue("company_id", companyId.Value);
            deleteCommand.Parameters.AddWithValue("discrepancy_type", discrepancyType);
            deleteCommand.Parameters.Add(new NpgsqlParameter<Guid[]>("source_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = sourceDocumentIds
            });
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (sourceDocumentId, row) in rows)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into inventory_outbound_discrepancy_lanes (
                  id,
                  company_id,
                  discrepancy_type,
                  source_document_id,
                  item_id,
                  warehouse_id,
                  uom_code,
                  status,
                  source_quantity,
                  matched_quantity,
                  remaining_quantity,
                  summary,
                  latest_matched_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @discrepancy_type,
                  @source_document_id,
                  @item_id,
                  @warehouse_id,
                  @uom_code,
                  @status,
                  @source_quantity,
                  @matched_quantity,
                  @remaining_quantity,
                  @summary,
                  @latest_matched_at,
                  @updated_at
                );
                """;
            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertCommand.Parameters.AddWithValue("discrepancy_type", discrepancyType);
            insertCommand.Parameters.AddWithValue("source_document_id", sourceDocumentId);
            insertCommand.Parameters.AddWithValue("item_id", row.ItemId);
            insertCommand.Parameters.AddWithValue("warehouse_id", row.WarehouseId);
            insertCommand.Parameters.AddWithValue("uom_code", row.UomCode);
            insertCommand.Parameters.AddWithValue("status", row.Status);
            insertCommand.Parameters.AddWithValue("source_quantity", row.SourceQuantity);
            insertCommand.Parameters.AddWithValue("matched_quantity", row.MatchedQuantity);
            insertCommand.Parameters.AddWithValue("remaining_quantity", row.RemainingQuantity);
            insertCommand.Parameters.AddWithValue("summary", row.Summary);
            insertCommand.Parameters.AddWithValue("latest_matched_at", row.LatestMatchedAt ?? (object)DBNull.Value);
            insertCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, string?>> LoadInvoiceStatusMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid[] invoiceDocumentIds,
        CancellationToken cancellationToken)
    {
        if (invoiceDocumentIds.Length == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, status
            from invoices
            where company_id = @company_id
              and id = any(@invoice_document_ids);
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("invoice_document_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = invoiceDocumentIds
        });

        var rows = new Dictionary<Guid, string?>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetGuid(reader.GetOrdinal("id"))] = reader.GetString(reader.GetOrdinal("status"));
        }

        return rows;
    }

    private static async Task<InvoiceShipmentCoverageRow> LoadInvoiceShipmentCoverageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        var (invoiceOutboundLineCount, invoiceOutboundQuantity) = await LoadInvoiceOutboundSummaryAsync(connection, transaction, companyId, invoiceDocumentId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              coalesce(sum(matched_document_count), 0)::int as shipment_count,
              coalesce(sum(matched_quantity), 0) as shipped_quantity,
              max(latest_matched_at) as latest_matched_at
            from inventory_outbound_matching_lanes
            where company_id = @company_id
              and lane_type = 'invoice_shipment'
              and source_document_id = @invoice_document_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var shipmentCount = reader.GetInt32(reader.GetOrdinal("shipment_count"));
        var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
        var remainingQuantity = decimal.Round(invoiceOutboundQuantity - shippedQuantity, 6, MidpointRounding.AwayFromZero);
        var status = ResolveInvoiceShipmentDocumentStatus(
            invoiceOutboundLineCount,
            shipmentCount,
            invoiceOutboundQuantity,
            shippedQuantity);

        return new InvoiceShipmentCoverageRow(
            invoiceOutboundLineCount,
            invoiceOutboundQuantity,
            shipmentCount,
            shippedQuantity,
            remainingQuantity,
            status,
            reader.IsDBNull(reader.GetOrdinal("latest_matched_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_matched_at")));
    }

    private static async Task<InvoiceCoverageRow> LoadInvoiceCoverageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid invoiceDocumentId,
        InvoiceShipmentCoverageRow shipmentCoverage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select status, posted_at
            from invoices
            where company_id = @company_id
              and id = @invoice_document_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new InvoiceCoverageRow(
                0m,
                0m,
                shipmentCoverage.MatchStatus,
                null,
                null);
        }

        var invoiceStatus = reader.GetString(reader.GetOrdinal("status"));
        DateTimeOffset? invoicePostedAt = reader.IsDBNull(reader.GetOrdinal("posted_at"))
            ? null
            : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"));
        var invoicedQuantity = decimal.Round(
            ResolveInvoicedQuantity(invoiceStatus, shipmentCoverage.InvoiceOutboundQuantity),
            6,
            MidpointRounding.AwayFromZero);
        var remainingToInvoiceQuantity = ResolveRemainingToInvoiceQuantity(
            invoiceStatus,
            shipmentCoverage.ShippedQuantity,
            shipmentCoverage.InvoiceOutboundQuantity);
        var status = ResolveInvoiceCoverageStatus(
            shipmentCoverage.InvoiceOutboundLineCount,
            invoiceStatus,
            shipmentCoverage.ShippedQuantity,
            shipmentCoverage.InvoiceOutboundQuantity);

        return new InvoiceCoverageRow(
            invoicedQuantity,
            remainingToInvoiceQuantity,
            status,
            invoiceStatus,
            invoicePostedAt);
    }

    private static async Task<ShipmentIssueCoverageRow> LoadShipmentIssueCoverageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        await using var sourceCommand = connection.CreateCommand();
        sourceCommand.Transaction = transaction;
        sourceCommand.CommandText =
            """
            select
              count(*)::int as shipment_count,
              coalesce(sum(base_quantity), 0) as shipped_quantity
            from inventory_document_lines
            where company_id = @company_id
              and document_id = @shipment_document_id;
            """;
        sourceCommand.Parameters.AddWithValue("company_id", companyId.Value);
        sourceCommand.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        await using var sourceReader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
        await sourceReader.ReadAsync(cancellationToken);
        var shipmentCount = sourceReader.GetInt32(sourceReader.GetOrdinal("shipment_count"));
        var shippedQuantity = decimal.Round(sourceReader.GetFieldValue<decimal>(sourceReader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
        await sourceReader.DisposeAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              coalesce(sum(matched_document_count), 0)::int as issue_count,
              coalesce(sum(matched_quantity), 0) as issued_quantity,
              max(latest_matched_at) as latest_matched_at
            from inventory_outbound_matching_lanes
            where company_id = @company_id
              and lane_type = 'shipment_issue'
              and source_document_id = @shipment_document_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var issueCount = reader.GetInt32(reader.GetOrdinal("issue_count"));
        var issuedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("issued_quantity")), 6, MidpointRounding.AwayFromZero);
        var remainingQuantity = decimal.Round(shippedQuantity - issuedQuantity, 6, MidpointRounding.AwayFromZero);
        var status = ResolveShipmentIssueDocumentStatus(
            shipmentCount,
            issueCount,
            shippedQuantity,
            issuedQuantity);

        return new ShipmentIssueCoverageRow(
            shipmentCount,
            shippedQuantity,
            issueCount,
            issuedQuantity,
            remainingQuantity,
            status,
            reader.IsDBNull(reader.GetOrdinal("latest_matched_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_matched_at")));
    }

    private static async Task<IReadOnlyList<InventoryShipmentIssueLineSummary>> LoadShipmentIssueLineSummariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              lane.item_id,
              i.item_code,
              i.name as item_name,
              lane.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              lane.uom_code,
              lane.source_line_count as shipment_line_count,
              lane.source_quantity as shipment_quantity,
              lane.matched_quantity as issued_quantity,
              lane.remaining_quantity as remaining_to_issue_quantity,
              lane.status as match_status
            from inventory_outbound_matching_lanes lane
            join inventory_items i
              on i.id = lane.item_id
             and i.company_id = lane.company_id
            join inventory_warehouses w
              on w.id = lane.warehouse_id
             and w.company_id = lane.company_id
            where lane.company_id = @company_id
              and lane.lane_type = 'shipment_issue'
              and lane.source_document_id = @shipment_document_id
            order by i.item_code, w.warehouse_code, lane.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        var rows = new List<InventoryShipmentIssueLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InventoryShipmentIssueLineSummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetInt32(reader.GetOrdinal("shipment_line_count")),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipment_quantity")), 6, MidpointRounding.AwayFromZero),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("issued_quantity")), 6, MidpointRounding.AwayFromZero),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_to_issue_quantity")), 6, MidpointRounding.AwayFromZero),
                reader.GetString(reader.GetOrdinal("match_status"))));
        }

        return rows;
    }

    private static async Task<ShipmentIssueCoverageRow> LoadInvoiceIssueCoverageAsync(
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
            with invoice_shipments as (
              select id
              from inventory_documents
              where company_id = @company_id
                and document_type = 'shipment'
                and source_module = 'ar_invoice'
                and source_document_id = @invoice_document_id
            ),
            shipment_totals as (
              select
                count(distinct d.id)::int as shipment_count,
                coalesce(sum(l.base_quantity), 0) as shipped_quantity
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.id in (select id from invoice_shipments)
            ),
            issue_totals as (
              select
                coalesce(sum(lane.matched_document_count), 0)::int as issue_count,
                coalesce(sum(lane.matched_quantity), 0) as issued_quantity,
                max(lane.latest_matched_at) as latest_matched_at
              from inventory_outbound_matching_lanes lane
              where lane.company_id = @company_id
                and lane.lane_type = 'shipment_issue'
                and lane.source_document_id in (select id from invoice_shipments)
            )
            select
              coalesce((select shipment_count from shipment_totals), 0) as shipment_count,
              coalesce((select shipped_quantity from shipment_totals), 0) as shipped_quantity,
              coalesce((select issue_count from issue_totals), 0) as issue_count,
              coalesce((select issued_quantity from issue_totals), 0) as issued_quantity,
              (select latest_matched_at from issue_totals) as latest_matched_at;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var shipmentCount = reader.GetInt32(reader.GetOrdinal("shipment_count"));
        var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
        var issueCount = reader.GetInt32(reader.GetOrdinal("issue_count"));
        var issuedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("issued_quantity")), 6, MidpointRounding.AwayFromZero);
        var remainingQuantity = decimal.Round(shippedQuantity - issuedQuantity, 6, MidpointRounding.AwayFromZero);
        var status = ResolveShipmentIssueDocumentStatus(
            shipmentCount,
            issueCount,
            shippedQuantity,
            issuedQuantity);

        return new ShipmentIssueCoverageRow(
            shipmentCount,
            shippedQuantity,
            issueCount,
            issuedQuantity,
            remainingQuantity,
            status,
            reader.IsDBNull(reader.GetOrdinal("latest_matched_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_matched_at")));
    }

    private static async Task<IReadOnlyList<InventoryInvoiceShipmentIssueLineLaneSummary>> LoadInvoiceIssueLaneSummariesAsync(
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
            with invoice_header as (
              select status as invoice_status
              from invoices
              where company_id = @company_id
                and id = @invoice_document_id
              limit 1
            ),
            invoice_lanes as (
              select
                lane.item_id,
                i.item_code,
                i.name as item_name,
                lane.warehouse_id,
                w.warehouse_code,
                w.name as warehouse_name,
                lane.uom_code,
                lane.source_line_count as invoice_line_count,
                lane.source_quantity as invoice_quantity,
                lane.matched_quantity as shipped_quantity,
                lane.remaining_quantity as remaining_to_ship_quantity,
                lane.status as shipment_match_status
              from inventory_outbound_matching_lanes lane
              join inventory_items i
                on i.id = lane.item_id
               and i.company_id = lane.company_id
              join inventory_warehouses w
                on w.id = lane.warehouse_id
               and w.company_id = lane.company_id
              where lane.company_id = @company_id
                and lane.lane_type = 'invoice_shipment'
                and lane.source_document_id = @invoice_document_id
            ),
            issue_lanes as (
              select
                s.source_document_id as invoice_document_id,
                lane.item_id,
                lane.warehouse_id,
                lane.uom_code,
                coalesce(sum(lane.matched_quantity), 0) as issued_quantity
              from inventory_documents s
              join inventory_outbound_matching_lanes lane
                on lane.source_document_id = s.id
               and lane.company_id = s.company_id
              where s.company_id = @company_id
                and s.document_type = 'shipment'
                and s.source_module = 'ar_invoice'
                and s.source_document_id = @invoice_document_id
                and lane.lane_type = 'shipment_issue'
              group by s.source_document_id, lane.item_id, lane.warehouse_id, lane.uom_code
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
              i.shipped_quantity,
              i.remaining_to_ship_quantity,
              i.shipment_match_status,
              coalesce((select invoice_status from invoice_header), 'draft') as invoice_status,
              coalesce(il.issued_quantity, 0) as issued_quantity
            from invoice_lanes i
            left join issue_lanes il
              on il.invoice_document_id = @invoice_document_id
             and il.item_id = i.item_id
             and il.warehouse_id = i.warehouse_id
             and il.uom_code = i.uom_code
            order by i.item_code, i.warehouse_code, i.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var rows = new List<InventoryInvoiceShipmentIssueLineLaneSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invoiceStatus = reader.GetString(reader.GetOrdinal("invoice_status"));
            var invoiceQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("invoice_quantity")), 6, MidpointRounding.AwayFromZero);
            var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
            var issuedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("issued_quantity")), 6, MidpointRounding.AwayFromZero);
            var invoicedQuantity = decimal.Round(ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity), 6, MidpointRounding.AwayFromZero);
            rows.Add(new InventoryInvoiceShipmentIssueLineLaneSummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                reader.GetInt32(reader.GetOrdinal("invoice_line_count")),
                invoiceQuantity,
                shippedQuantity,
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_to_ship_quantity")), 6, MidpointRounding.AwayFromZero),
                reader.GetString(reader.GetOrdinal("shipment_match_status")),
                invoicedQuantity,
                ResolveRemainingToInvoiceQuantity(invoiceStatus, shippedQuantity, invoiceQuantity),
                ResolveInvoiceCoverageLineStatus(invoiceStatus, shippedQuantity, invoiceQuantity),
                issuedQuantity,
                decimal.Round(shippedQuantity - issuedQuantity, 6, MidpointRounding.AwayFromZero),
                ResolveShipmentIssueLineStatus(shippedQuantity, issuedQuantity)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<InventoryOutboundDiscrepancySummary>> LoadInvoiceOutboundDiscrepanciesAsync(
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
            with invoice_shipments as (
              select id
              from inventory_documents
              where company_id = @company_id
                and document_type = 'shipment'
                and source_module = 'ar_invoice'
                and source_document_id = @invoice_document_id
            ),
            target_discrepancies as (
              select
                lane.discrepancy_type,
                lane.source_document_id,
                lane.item_id,
                lane.warehouse_id,
                lane.uom_code,
                lane.status,
                lane.source_quantity,
                lane.matched_quantity,
                lane.remaining_quantity,
                lane.summary,
                lane.latest_matched_at
              from inventory_outbound_discrepancy_lanes lane
              where lane.company_id = @company_id
                and (
                  (lane.discrepancy_type in ('invoice_shipment', 'invoice_coverage') and lane.source_document_id = @invoice_document_id)
                  or
                  (lane.discrepancy_type = 'shipment_issue' and lane.source_document_id in (select id from invoice_shipments))
                )
            )
            select
              lane.discrepancy_type as lane_type,
              lane.source_document_id,
              lane.item_id,
              i.item_code,
              i.name as item_name,
              lane.warehouse_id,
              w.warehouse_code,
              w.name as warehouse_name,
              lane.uom_code,
              lane.status,
              lane.source_quantity,
              lane.matched_quantity,
              lane.remaining_quantity,
              lane.summary,
              lane.latest_matched_at
            from target_discrepancies lane
            join inventory_items i
              on i.id = lane.item_id
             and i.company_id = @company_id
            join inventory_warehouses w
              on w.id = lane.warehouse_id
             and w.company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_document_id", invoiceDocumentId);

        var rows = new List<InventoryOutboundDiscrepancySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var laneType = reader.GetString(reader.GetOrdinal("lane_type"));
            var status = reader.GetString(reader.GetOrdinal("status"));
            var sourceQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("source_quantity")), 6, MidpointRounding.AwayFromZero);
            var matchedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("matched_quantity")), 6, MidpointRounding.AwayFromZero);
            var remainingQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("remaining_quantity")), 6, MidpointRounding.AwayFromZero);
            rows.Add(new InventoryOutboundDiscrepancySummary(
                laneType,
                reader.GetGuid(reader.GetOrdinal("source_document_id")),
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                status,
                sourceQuantity,
                matchedQuantity,
                remainingQuantity,
                reader.IsDBNull(reader.GetOrdinal("latest_matched_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("latest_matched_at")),
                reader.GetString(reader.GetOrdinal("summary"))));
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
            with invoice_shipments as (
              select id
              from inventory_documents
              where company_id = @company_id
                and document_type = 'shipment'
                and source_module = 'ar_invoice'
                and source_document_id = @invoice_document_id
            )
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
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.document_type = 'sales_issue'
              and d.source_module = 'inventory_shipment'
              and d.source_document_id in (select id from invoice_shipments)
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

        return await ReadIssueSummariesAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<InventorySalesIssueSummary>> LoadShipmentAnchoredIssuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid shipmentDocumentId,
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
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.document_type = 'sales_issue'
              and d.source_module = 'inventory_shipment'
              and d.source_document_id = @shipment_document_id
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
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        return await ReadIssueSummariesAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<InventorySalesIssueSummary>> ReadIssueSummariesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
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
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_cost_base")), 6, MidpointRounding.AwayFromZero),
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

    private static string BuildDiscrepancySummary(
        string laneType,
        string status,
        decimal sourceQuantity,
        decimal matchedQuantity,
        decimal remainingQuantity) =>
        (laneType, status) switch
        {
            ("invoice_shipment", "over_shipped") => $"Anchored shipment truth exceeds invoice quantity ({matchedQuantity:N2} shipped against {sourceQuantity:N2} invoiced).",
            ("invoice_coverage", "over_invoiced") => $"Posted invoice truth exceeds shipped quantity ({matchedQuantity:N2} invoiced against {sourceQuantity:N2} shipped).",
            ("shipment_issue", "over_issued") => $"Sales issue truth exceeds shipped quantity ({matchedQuantity:N2} issued against {sourceQuantity:N2} shipped).",
            _ => $"Outbound lane requires review. Remaining quantity: {remainingQuantity:N2}."
        };

    private static string BuildShipmentNumber(DateOnly postingDate) =>
        $"SHP-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private sealed record InvoiceShipmentCoverageRow(
        int InvoiceOutboundLineCount,
        decimal InvoiceOutboundQuantity,
        int ShipmentCount,
        decimal ShippedQuantity,
        decimal RemainingQuantity,
        string MatchStatus,
        DateTimeOffset? LatestMatchedAt);

    private sealed record InvoiceCoverageRow(
        decimal InvoicedQuantity,
        decimal RemainingToInvoiceQuantity,
        string Status,
        string? InvoiceStatus,
        DateTimeOffset? InvoicePostedAt);

    private sealed record ShipmentIssueCoverageRow(
        int ShipmentCount,
        decimal ShippedQuantity,
        int IssueCount,
        decimal IssuedQuantity,
        decimal RemainingQuantity,
        string MatchStatus,
        DateTimeOffset? LatestMatchedAt);

    private sealed record MatchingLaneRow(
        Guid ItemId,
        Guid WarehouseId,
        string UomCode,
        int SourceLineCount,
        decimal SourceQuantity,
        int MatchedDocumentCount,
        decimal MatchedQuantity,
        decimal RemainingQuantity,
        string Status,
        DateTimeOffset? LatestMatchedAt);

    private sealed record DiscrepancyLaneRow(
        Guid ItemId,
        Guid WarehouseId,
        string UomCode,
        string Status,
        decimal SourceQuantity,
        decimal MatchedQuantity,
        decimal RemainingQuantity,
        DateTimeOffset? LatestMatchedAt,
        string Summary);

    private static string ResolveInvoiceShipmentDocumentStatus(
        int invoiceOutboundLineCount,
        int shipmentCount,
        decimal invoiceQuantity,
        decimal shippedQuantity)
    {
        if (invoiceOutboundLineCount <= 0)
        {
            return "no_inventory_handoff";
        }

        return ResolveCoverageStatus(
            shipmentCount,
            invoiceQuantity,
            shippedQuantity,
            "no_shipment",
            "partially_shipped",
            "fully_shipped",
            "over_shipped");
    }

    private static string ResolveInvoiceShipmentLineStatus(
        decimal invoiceQuantity,
        decimal shippedQuantity) =>
        ResolveCoverageStatus(
            shippedQuantity > 0m ? 1 : 0,
            invoiceQuantity,
            shippedQuantity,
            "no_shipment",
            "partially_shipped",
            "fully_shipped",
            "over_shipped");

    private static decimal ResolveInvoicedQuantity(string? invoiceStatus, decimal invoiceQuantity) =>
        string.Equals(invoiceStatus, "posted", StringComparison.OrdinalIgnoreCase)
            ? invoiceQuantity
            : 0m;

    private static string ResolveInvoiceCoverageStatus(
        int invoiceOutboundLineCount,
        string? invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity)
    {
        if (invoiceOutboundLineCount <= 0)
        {
            return "no_inventory_handoff";
        }

        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        var invoicedQuantity = ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity);
        if (invoicedQuantity <= 0m)
        {
            return "not_invoiced";
        }

        return ResolveInvoiceCoverageLineStatus(invoiceStatus, shippedQuantity, invoiceQuantity);
    }

    private static string ResolveInvoiceCoverageLineStatus(
        string? invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity)
    {
        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        var invoicedQuantity = ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity);
        if (invoicedQuantity <= 0m)
        {
            return "not_invoiced";
        }

        if (invoicedQuantity < shippedQuantity)
        {
            return "partially_invoiced";
        }

        if (invoicedQuantity == shippedQuantity)
        {
            return "fully_invoiced";
        }

        return "over_invoiced";
    }

    private static decimal ResolveRemainingToInvoiceQuantity(
        string? invoiceStatus,
        decimal shippedQuantity,
        decimal invoiceQuantity) =>
        decimal.Round(
            Math.Max(0m, shippedQuantity - ResolveInvoicedQuantity(invoiceStatus, invoiceQuantity)),
            6,
            MidpointRounding.AwayFromZero);

    private static string ResolveShipmentIssueDocumentStatus(
        int shipmentCount,
        int issueCount,
        decimal shippedQuantity,
        decimal issuedQuantity)
    {
        if (shipmentCount <= 0 || shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        return ResolveCoverageStatus(
            issueCount,
            shippedQuantity,
            issuedQuantity,
            "pending_issue",
            "partially_issued",
            "fully_issued",
            "over_issued");
    }

    private static string ResolveShipmentIssueLineStatus(
        decimal shippedQuantity,
        decimal issuedQuantity)
    {
        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        return ResolveCoverageStatus(
            issuedQuantity > 0m ? 1 : 0,
            shippedQuantity,
            issuedQuantity,
            "pending_issue",
            "partially_issued",
            "fully_issued",
            "over_issued");
    }

    private static string ResolveCoverageStatus(
        int matchedDocumentCount,
        decimal sourceQuantity,
        decimal matchedQuantity,
        string noneStatus,
        string partialStatus,
        string fullStatus,
        string overStatus)
    {
        var roundedSource = decimal.Round(sourceQuantity, 6, MidpointRounding.AwayFromZero);
        var roundedMatched = decimal.Round(matchedQuantity, 6, MidpointRounding.AwayFromZero);
        var remaining = decimal.Round(roundedSource - roundedMatched, 6, MidpointRounding.AwayFromZero);

        if (matchedDocumentCount <= 0 || roundedMatched <= 0m)
        {
            return noneStatus;
        }

        if (remaining > 0m)
        {
            return partialStatus;
        }

        if (remaining == 0m)
        {
            return fullStatus;
        }

        return overStatus;
    }

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
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
}
