using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Domain.Shared;
using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory;

public sealed class PostgreSqlInventoryReturnStore : IInventoryReturnStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly IInventoryFoundationStore _foundationStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaEnsured;

    public PostgreSqlInventoryReturnStore(
        PostgreSqlConnectionFactory connections,
        IInventoryFoundationStore foundationStore)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _foundationStore = foundationStore ?? throw new ArgumentNullException(nameof(foundationStore));
    }

    public async Task<InventoryReturnReceiveDashboard> GetDashboardAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var recentShipments = await LoadRecentShipmentsAsync(connection, null, companyId, cancellationToken);
        var recentReturns = await LoadRecentReturnsAsync(connection, null, companyId, cancellationToken);

        return new InventoryReturnReceiveDashboard(
            companyId,
            recentShipments,
            recentReturns);
    }

    public async Task<InventoryReturnReceiveHandoffSummary> GetShipmentHandoffSummaryAsync(
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        _ = await _foundationStore.GetSummaryAsync(companyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        return await LoadShipmentHandoffSummaryAsync(connection, null, companyId, shipmentDocumentId, cancellationToken);
    }

    public async Task<InventoryReturnReceiveSummary> PostAsync(
        InventoryReturnReceivePostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = await _foundationStore.GetSummaryAsync(request.CompanyId, cancellationToken);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var handoff = await LoadShipmentHandoffSummaryAsync(
                connection,
                transaction,
                request.CompanyId,
                request.ShipmentDocumentId,
                cancellationToken);

            if (handoff.CustomerId != request.CustomerId)
            {
                throw new InvalidOperationException("Return receive must stay anchored to the shipment customer.");
            }

            var now = DateTimeOffset.UtcNow;
            var documentId = Guid.NewGuid();
            var documentNumber = BuildReturnNumber(request.PostingDate);

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
                      'customer_return_receipt',
                      'posted',
                      'inbound',
                      @posting_date,
                      'inventory_shipment',
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
                insertDocumentCommand.Parameters.AddWithValue("source_document_id", request.ShipmentDocumentId);
                insertDocumentCommand.Parameters.AddWithValue("source_document_number", handoff.ShipmentDocumentNumber);
                insertDocumentCommand.Parameters.AddWithValue("counterparty_id", request.CustomerId);
                insertDocumentCommand.Parameters.AddWithValue("memo", ToDbValue(request.Memo));
                insertDocumentCommand.Parameters.AddWithValue("created_by_user_id", request.UserId);
                insertDocumentCommand.Parameters.AddWithValue("created_at", now);
                insertDocumentCommand.Parameters.AddWithValue("posted_at", now);
                await insertDocumentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var line in request.Lines.OrderBy(line => line.LineNo))
            {
                var anchorLine = handoff.LineSummaries.FirstOrDefault(candidate =>
                    candidate.ItemId == line.ItemId &&
                    candidate.WarehouseId == line.WarehouseId &&
                    string.Equals(candidate.UomCode, line.UomCode, StringComparison.OrdinalIgnoreCase));

                if (anchorLine is null)
                {
                    throw new InvalidOperationException("Return receive line must stay inside the anchored shipment truth.");
                }

                if (line.Quantity > anchorLine.RemainingReturnableQuantity)
                {
                    throw new InvalidOperationException(
                        $"Return receive cannot exceed the remaining returnable quantity for '{anchorLine.ItemName}'.");
                }

                await InsertDocumentLineAsync(
                    connection,
                    transaction,
                    request.CompanyId,
                    documentId,
                    line,
                    cancellationToken);
            }

            var refreshedHandoff = await LoadShipmentHandoffSummaryAsync(
                connection,
                transaction,
                request.CompanyId,
                request.ShipmentDocumentId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new InventoryReturnReceiveSummary(
                documentId,
                request.CompanyId,
                documentNumber,
                "posted",
                request.PostingDate,
                request.CustomerId,
                handoff.CustomerDisplayName,
                request.ShipmentDocumentId,
                handoff.ShipmentDocumentNumber,
                decimal.Round(request.Lines.Sum(static line => line.Quantity), 6, MidpointRounding.AwayFromZero),
                request.Lines.Count,
                now,
                now,
                refreshedHandoff.ReturnedQuantity,
                refreshedHandoff.RemainingReturnableQuantity,
                refreshedHandoff.MatchStatus,
                request.Memo,
                request.Lines);
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

                alter table inventory_document_lines
                  add column if not exists condition_code text null;

                alter table inventory_document_lines
                  add column if not exists return_reason_code text null;

                alter table inventory_document_lines
                  add column if not exists disposition_reason_code text null;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task<InventoryReturnReceiveHandoffSummary> LoadShipmentHandoffSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid shipmentDocumentId,
        CancellationToken cancellationToken)
    {
        var shipmentHeader = await LoadShipmentHeaderAsync(connection, transaction, companyId, shipmentDocumentId, cancellationToken)
            ?? throw new InvalidOperationException("Shipment anchor must exist inside the active company before return receive can start.");
        var lineSummaries = await LoadShipmentReturnLineSummariesAsync(connection, transaction, companyId, shipmentDocumentId, cancellationToken);
        var recentReturns = await LoadShipmentAnchoredReturnsAsync(connection, transaction, companyId, shipmentDocumentId, cancellationToken);
        var returnedQuantity = decimal.Round(lineSummaries.Sum(static line => line.ReturnedQuantity), 6, MidpointRounding.AwayFromZero);
        var remainingReturnableQuantity = ResolveRemainingReturnableQuantity(shipmentHeader.TotalQuantity, returnedQuantity);
        var matchStatus = ResolveReturnMatchStatus(shipmentHeader.LineCount, shipmentHeader.TotalQuantity, returnedQuantity);

        return new InventoryReturnReceiveHandoffSummary(
            shipmentHeader.DocumentId,
            shipmentHeader.DocumentNumber,
            shipmentHeader.CustomerId,
            shipmentHeader.CustomerDisplayName,
            shipmentHeader.PostingDate,
            shipmentHeader.LineCount,
            shipmentHeader.TotalQuantity,
            recentReturns.Count,
            returnedQuantity,
            remainingReturnableQuantity,
            matchStatus,
            recentReturns.FirstOrDefault()?.PostedAt,
            recentReturns,
            lineSummaries);
    }

    private static async Task<ShipmentHeaderSnapshot?> LoadShipmentHeaderAsync(
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
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              d.posting_date,
              d.counterparty_id as customer_id,
              coalesce(c.display_name, 'Unknown customer') as customer_display_name,
              count(l.id)::int as line_count,
              coalesce(sum(l.base_quantity), 0) as total_quantity
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_document_lines l
              on l.document_id = d.id
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.id = @shipment_document_id
              and d.document_type = 'shipment'
              and d.status = 'posted'
            group by
              d.id,
              d.document_number,
              d.source_document_number,
              d.posting_date,
              d.counterparty_id,
              c.display_name
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ShipmentHeaderSnapshot(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("document_number")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
            reader.GetGuid(reader.GetOrdinal("customer_id")),
            reader.GetString(reader.GetOrdinal("customer_display_name")),
            reader.GetInt32(reader.GetOrdinal("line_count")),
            decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero));
    }

    private static async Task<IReadOnlyList<InventoryReturnReceiveHandoffLineSummary>> LoadShipmentReturnLineSummariesAsync(
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
                  l.item_id,
                  i.item_code,
                  i.name as item_name,
                  l.warehouse_id,
                  w.warehouse_code,
                  w.name as warehouse_name,
                  upper(l.uom_code) as uom_code,
                  coalesce(sum(l.base_quantity), 0) as shipped_quantity
                from inventory_document_lines l
                join inventory_items i
                  on i.id = l.item_id
                 and i.company_id = l.company_id
                join inventory_warehouses w
                  on w.id = l.warehouse_id
                 and w.company_id = l.company_id
                where l.company_id = @company_id
                  and l.document_id = @shipment_document_id
                group by
                  l.item_id,
                  i.item_code,
                  i.name,
                  l.warehouse_id,
                  w.warehouse_code,
                  w.name,
                  upper(l.uom_code)
            ),
            return_groups as (
                select
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code) as uom_code,
                  coalesce(sum(l.base_quantity), 0) as returned_quantity
                from inventory_documents d
                join inventory_document_lines l
                  on l.document_id = d.id
                 and l.company_id = d.company_id
                where d.company_id = @company_id
                  and d.document_type = 'customer_return_receipt'
                  and d.source_module = 'inventory_shipment'
                  and d.source_document_id = @shipment_document_id
                group by
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code)
            )
            select
              s.item_id,
              s.item_code,
              s.item_name,
              s.warehouse_id,
              s.warehouse_code,
              s.warehouse_name,
              s.uom_code,
              s.shipped_quantity,
              coalesce(r.returned_quantity, 0) as returned_quantity
            from shipment_groups s
            left join return_groups r
              on r.item_id = s.item_id
             and r.warehouse_id = s.warehouse_id
             and r.uom_code = s.uom_code
            order by s.item_code, s.warehouse_code, s.uom_code;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        var rows = new List<InventoryReturnReceiveHandoffLineSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
            var returnedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("returned_quantity")), 6, MidpointRounding.AwayFromZero);
            rows.Add(new InventoryReturnReceiveHandoffLineSummary(
                reader.GetGuid(reader.GetOrdinal("item_id")),
                reader.GetString(reader.GetOrdinal("item_code")),
                reader.GetString(reader.GetOrdinal("item_name")),
                reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                reader.GetString(reader.GetOrdinal("warehouse_code")),
                reader.GetString(reader.GetOrdinal("warehouse_name")),
                reader.GetString(reader.GetOrdinal("uom_code")),
                shippedQuantity,
                returnedQuantity,
                ResolveRemainingReturnableQuantity(shippedQuantity, returnedQuantity),
                ResolveReturnLineMatchStatus(shippedQuantity, returnedQuantity)));
        }

        return rows;
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
              and d.status = 'posted'
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
            var totalQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero);
            shipments.Add(new InventoryShipmentSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_display_name")),
                totalQuantity,
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
                totalQuantity,
                "pending_issue",
                null,
                Array.Empty<InventoryShipmentIssueLineSummary>(),
                Array.Empty<InventorySalesIssueSummary>(),
                Array.Empty<InventoryShipmentLineInput>()));
        }

        return shipments;
    }

    private static async Task<IReadOnlyList<InventoryReturnReceiveSummary>> LoadRecentReturnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            with shipment_totals as (
              select
                d.id as shipment_document_id,
                coalesce(sum(l.base_quantity), 0) as shipped_quantity
              from inventory_documents d
              left join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'shipment'
              group by d.id
            ),
            return_totals as (
              select
                d.source_document_id as shipment_document_id,
                coalesce(sum(l.base_quantity), 0) as returned_quantity
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'customer_return_receipt'
                and d.source_module = 'inventory_shipment'
              group by d.source_document_id
            )
            select
              d.id,
              d.company_id,
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              d.status,
              d.posting_date,
              d.counterparty_id as customer_id,
              coalesce(c.display_name, 'Unknown customer') as customer_display_name,
              d.source_document_id as shipment_document_id,
              coalesce(s.document_number, d.source_document_number, 'UNNUMBERED') as shipment_document_number,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              coalesce(rt.returned_quantity, 0) as returned_quantity,
              coalesce(st.shipped_quantity, 0) as shipped_quantity,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_documents s
              on s.id = d.source_document_id
             and s.company_id = d.company_id
            left join inventory_document_lines l
              on l.document_id = d.id
             and l.company_id = d.company_id
            left join shipment_totals st
              on st.shipment_document_id = d.source_document_id
            left join return_totals rt
              on rt.shipment_document_id = d.source_document_id
            where d.company_id = @company_id
              and d.document_type = 'customer_return_receipt'
            group by
              d.id,
              d.company_id,
              d.document_number,
              d.source_document_number,
              d.status,
              d.posting_date,
              d.counterparty_id,
              c.display_name,
              d.source_document_id,
              s.document_number,
              d.created_at,
              d.posted_at,
              rt.returned_quantity,
              st.shipped_quantity,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 10;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        return await ReadReturnSummariesAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<InventoryReturnReceiveSummary>> LoadShipmentAnchoredReturnsAsync(
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
            with shipment_totals as (
              select coalesce(sum(l.base_quantity), 0) as shipped_quantity
              from inventory_document_lines l
              where l.company_id = @company_id
                and l.document_id = @shipment_document_id
            ),
            return_totals as (
              select coalesce(sum(l.base_quantity), 0) as returned_quantity
              from inventory_documents d
              join inventory_document_lines l
                on l.document_id = d.id
               and l.company_id = d.company_id
              where d.company_id = @company_id
                and d.document_type = 'customer_return_receipt'
                and d.source_module = 'inventory_shipment'
                and d.source_document_id = @shipment_document_id
            )
            select
              d.id,
              d.company_id,
              coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
              d.status,
              d.posting_date,
              d.counterparty_id as customer_id,
              coalesce(c.display_name, 'Unknown customer') as customer_display_name,
              d.source_document_id as shipment_document_id,
              coalesce(s.document_number, d.source_document_number, 'UNNUMBERED') as shipment_document_number,
              coalesce(sum(l.base_quantity), 0) as total_quantity,
              count(l.id) as line_count,
              d.created_at,
              d.posted_at,
              coalesce((select returned_quantity from return_totals), 0) as returned_quantity,
              coalesce((select shipped_quantity from shipment_totals), 0) as shipped_quantity,
              d.memo
            from inventory_documents d
            left join customers c
              on c.id = d.counterparty_id
            left join inventory_documents s
              on s.id = d.source_document_id
             and s.company_id = d.company_id
            left join inventory_document_lines l
              on l.document_id = d.id
             and l.company_id = d.company_id
            where d.company_id = @company_id
              and d.document_type = 'customer_return_receipt'
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
              d.source_document_id,
              s.document_number,
              d.created_at,
              d.posted_at,
              d.memo
            order by d.posting_date desc, d.created_at desc
            limit 5;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("shipment_document_id", shipmentDocumentId);

        return await ReadReturnSummariesAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<InventoryReturnReceiveSummary>> ReadReturnSummariesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var returns = new List<InventoryReturnReceiveSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var shippedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
            var returnedQuantity = decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("returned_quantity")), 6, MidpointRounding.AwayFromZero);
            returns.Add(new InventoryReturnReceiveSummary(
                reader.GetGuid(reader.GetOrdinal("id")),
                CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                reader.GetString(reader.GetOrdinal("document_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_display_name")),
                reader.GetGuid(reader.GetOrdinal("shipment_document_id")),
                reader.GetString(reader.GetOrdinal("shipment_document_number")),
                decimal.Round(reader.GetFieldValue<decimal>(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                returnedQuantity,
                ResolveRemainingReturnableQuantity(shippedQuantity, returnedQuantity),
                ResolveReturnMatchStatus(shippedQuantity > 0m ? 1 : 0, shippedQuantity, returnedQuantity),
                reader.IsDBNull(reader.GetOrdinal("memo"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("memo")),
                Array.Empty<InventoryReturnReceiveLineInput>()));
        }

        return returns;
    }

    private static async Task InsertDocumentLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        InventoryReturnReceiveLineInput line,
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
              memo,
              condition_code,
              return_reason_code,
              disposition_reason_code
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
              null,
              @memo,
              @condition_code,
              @return_reason_code,
              @disposition_reason_code
            );
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("line_no", line.LineNo);
        command.Parameters.AddWithValue("item_id", line.ItemId);
        command.Parameters.AddWithValue("warehouse_id", line.WarehouseId);
        command.Parameters.AddWithValue("uom_code", line.UomCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("quantity", line.Quantity);
        command.Parameters.AddWithValue("base_quantity", line.Quantity);
        command.Parameters.AddWithValue("memo", ToDbValue(line.Memo));
        command.Parameters.AddWithValue("condition_code", line.ConditionCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("return_reason_code", line.ReturnReasonCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("disposition_reason_code", ToDbValue(line.DispositionReasonCode));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildReturnNumber(DateOnly postingDate) =>
        $"CRR-{postingDate:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static decimal ResolveRemainingReturnableQuantity(
        decimal shippedQuantity,
        decimal returnedQuantity) =>
        decimal.Round(shippedQuantity - returnedQuantity, 6, MidpointRounding.AwayFromZero);

    private static string ResolveReturnMatchStatus(
        int shipmentLineCount,
        decimal shippedQuantity,
        decimal returnedQuantity)
    {
        if (shipmentLineCount == 0)
        {
            return "no_shipment";
        }

        if (returnedQuantity <= 0m)
        {
            return "not_returned";
        }

        if (returnedQuantity < shippedQuantity)
        {
            return "partially_returned";
        }

        if (returnedQuantity == shippedQuantity)
        {
            return "fully_returned";
        }

        return "over_returned";
    }

    private static string ResolveReturnLineMatchStatus(
        decimal shippedQuantity,
        decimal returnedQuantity)
    {
        if (shippedQuantity <= 0m)
        {
            return "no_shipment";
        }

        if (returnedQuantity <= 0m)
        {
            return "not_returned";
        }

        if (returnedQuantity < shippedQuantity)
        {
            return "partially_returned";
        }

        if (returnedQuantity == shippedQuantity)
        {
            return "fully_returned";
        }

        return "over_returned";
    }

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : value.Trim();

    private sealed record ShipmentHeaderSnapshot(
        Guid DocumentId,
        string DocumentNumber,
        DateOnly PostingDate,
        Guid CustomerId,
        string CustomerDisplayName,
        int LineCount,
        decimal TotalQuantity);
}
