using System.Text.Json;
using Infrastructure.PostgreSQL;
using Npgsql;

namespace Web.Shell.Services;

public sealed class ShellSalesCommercialDocumentClient(PostgreSqlConnectionFactory connections)
{
    private const string DocumentsTableName = "web_shell_sales_commercial_documents";
    private const string SequencesTableName = "web_shell_sales_commercial_document_sequences";
    private const string SalesOrderInvoiceAnchorsTableName = "web_shell_sales_order_invoice_anchors";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ShellSalesCommercialDocumentSummary>> ListAsync(
        Guid companyId,
        string? documentType,
        Guid? customerId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            select id, company_id, document_type, entity_number, display_number, status, customer_id,
                   document_date, expires_on, requested_ship_date, transaction_currency_code,
                   base_currency_code, source_quote_id, lines_json, updated_at
            from {DocumentsTableName}
            where company_id = @company_id
              and (@document_type is null or document_type = @document_type)
              and (@customer_id is null or customer_id = @customer_id)
            order by updated_at desc
            limit @take;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_type", string.IsNullOrWhiteSpace(documentType) ? DBNull.Value : NormalizeDocumentType(documentType));
        command.Parameters.AddWithValue("customer_id", customerId.HasValue ? customerId.Value : DBNull.Value);
        command.Parameters.AddWithValue("take", Math.Clamp(take, 1, 200));

        var rows = new List<ShellSalesCommercialDocumentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadSummary(reader));
        }

        return rows;
    }

    public async Task<ShellSalesCommercialDocumentReadModel?> GetAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            select id, company_id, document_type, entity_number, display_number, status, customer_id,
                   document_date, expires_on, requested_ship_date, transaction_currency_code,
                   base_currency_code, memo, source_quote_id, lines_json, updated_at
            from {DocumentsTableName}
            where company_id = @company_id
              and id = @id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadModel(reader) : null;
    }

    public async Task<ShellSalesCommercialDocumentReadModel> SaveAsync(
        Guid? documentId,
        ShellSalesCommercialDocumentSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSave(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var normalizedType = NormalizeDocumentType(request.DocumentType);
            var now = DateTimeOffset.UtcNow;
            var linePayload = JsonSerializer.Serialize(NormalizeLines(request.Lines), JsonOptions);

            if (documentId.HasValue)
            {
                var current = await GetForUpdateAsync(connection, transaction, request.CompanyId, documentId.Value, cancellationToken)
                    ?? throw new InvalidOperationException("The commercial document could not be found in the active company context.");
                if (current.DocumentType != normalizedType)
                {
                    throw new InvalidOperationException("The commercial document type cannot be changed after creation.");
                }

                if (current.Status is not ("draft" or "accepted"))
                {
                    throw new InvalidOperationException("Only draft or accepted commercial documents can be edited in this framework.");
                }

                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText =
                    $"""
                    update {DocumentsTableName}
                    set customer_id = @customer_id,
                        document_date = @document_date,
                        expires_on = @expires_on,
                        requested_ship_date = @requested_ship_date,
                        transaction_currency_code = @transaction_currency_code,
                        base_currency_code = @base_currency_code,
                        memo = @memo,
                        source_quote_id = @source_quote_id,
                        lines_json = @lines_json::jsonb,
                        updated_at = @updated_at
                    where company_id = @company_id
                      and id = @id;
                    """;
                BindSaveParameters(updateCommand, request, linePayload, now);
                updateCommand.Parameters.AddWithValue("id", documentId.Value);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return await GetAsync(request.CompanyId, documentId.Value, cancellationToken)
                    ?? throw new InvalidOperationException("The saved commercial document could not be reloaded.");
            }

            var id = Guid.NewGuid();
            var entityNumber = await ReserveEntityNumberAsync(connection, transaction, request.CompanyId, normalizedType, request.DocumentDate.Year, cancellationToken);
            var displayNumber = $"{GetDocumentPrefix(normalizedType)}-{entityNumber}";
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                $"""
                insert into {DocumentsTableName} (
                    id, company_id, document_type, entity_number, display_number, status, customer_id,
                    document_date, expires_on, requested_ship_date, transaction_currency_code,
                    base_currency_code, memo, source_quote_id, lines_json, created_by_user_id,
                    created_at, updated_at
                )
                values (
                    @id, @company_id, @document_type, @entity_number, @display_number, 'draft', @customer_id,
                    @document_date, @expires_on, @requested_ship_date, @transaction_currency_code,
                    @base_currency_code, @memo, @source_quote_id, @lines_json::jsonb, @created_by_user_id,
                    @updated_at, @updated_at
                );
                """;
            insertCommand.Parameters.AddWithValue("id", id);
            insertCommand.Parameters.AddWithValue("document_type", normalizedType);
            insertCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertCommand.Parameters.AddWithValue("display_number", displayNumber);
            BindSaveParameters(insertCommand, request, linePayload, now);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(request.CompanyId, id, cancellationToken)
                ?? throw new InvalidOperationException("The saved commercial document could not be reloaded.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ShellSalesCommercialDocumentReadModel> AcceptQuoteAsync(
        Guid companyId,
        Guid quoteId,
        CancellationToken cancellationToken = default) =>
        await SetStatusAsync(companyId, quoteId, "quote", "accepted", "accepted_at", cancellationToken);

    public async Task<ShellSalesCommercialDocumentReadModel> IssueSalesOrderAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken = default) =>
        await SetStatusAsync(companyId, salesOrderId, "sales_order", "issued", "issued_at", cancellationToken);

    public async Task<ShellSalesCommercialDocumentReadModel> CloseSalesOrderAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken = default)
    {
        var control = await GetSalesOrderOutboundControlAsync(companyId, salesOrderId, cancellationToken)
            ?? throw new InvalidOperationException("The sales order could not be found in the active company context.");
        if (!string.Equals(control.AggregateStatus, "closed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only fully shipped and fully invoiced sales orders can be closed.");
        }

        return await SetStatusAsync(companyId, salesOrderId, "sales_order", "closed", "closed_at", cancellationToken);
    }

    public async Task<ShellSalesCommercialDocumentReadModel> ConvertQuoteToSalesOrderAsync(
        Guid companyId,
        Guid quoteId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var quote = await GetAsync(companyId, quoteId, cancellationToken)
            ?? throw new InvalidOperationException("The quote could not be found in the active company context.");
        if (quote.DocumentType != "quote")
        {
            throw new InvalidOperationException("Only quotes can be converted into sales orders.");
        }

        var salesOrder = await SaveAsync(
            null,
            new ShellSalesCommercialDocumentSaveRequest
            {
                CompanyId = companyId,
                UserId = userId,
                DocumentType = "sales_order",
                CustomerId = quote.CustomerId,
                DocumentDate = DateOnly.FromDateTime(DateTime.Today),
                RequestedShipDate = quote.RequestedShipDate,
                TransactionCurrencyCode = quote.TransactionCurrencyCode,
                BaseCurrencyCode = quote.BaseCurrencyCode,
                Memo = $"Converted from quote {quote.DisplayNumber}. {quote.Memo}".Trim(),
                SourceQuoteId = quote.Id,
                Lines = quote.Lines.Select(static line => new ShellSalesCommercialDocumentLineSaveRequest
                {
                    LineNumber = line.LineNumber,
                    RevenueAccountId = line.RevenueAccountId,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    ItemId = line.ItemId,
                    WarehouseId = line.WarehouseId,
                    UomCode = line.UomCode
                }).ToArray()
            },
            cancellationToken);

        await SetStatusAsync(companyId, quoteId, "quote", "converted", "converted_at", cancellationToken);
        return salesOrder;
    }

    public async Task RecordSalesOrderInvoiceAnchorAsync(
        Guid companyId,
        Guid salesOrderId,
        ShellSalesCommercialInvoiceAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateInvoiceAnchor(companyId, salesOrderId, request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);

        var linePayload = JsonSerializer.Serialize(NormalizeInvoiceAnchorLines(request.Lines), JsonOptions);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            insert into {SalesOrderInvoiceAnchorsTableName} (
                company_id, sales_order_id, invoice_document_id, invoice_display_number, invoice_status,
                invoice_document_date, invoice_total_amount, invoice_quantity, lines_json, updated_at
            )
            values (
                @company_id, @sales_order_id, @invoice_document_id, @invoice_display_number, @invoice_status,
                @invoice_document_date, @invoice_total_amount, @invoice_quantity, @lines_json::jsonb, now()
            )
            on conflict (company_id, sales_order_id, invoice_document_id)
            do update set
                invoice_display_number = excluded.invoice_display_number,
                invoice_status = excluded.invoice_status,
                invoice_document_date = excluded.invoice_document_date,
                invoice_total_amount = excluded.invoice_total_amount,
                invoice_quantity = excluded.invoice_quantity,
                lines_json = excluded.lines_json,
                updated_at = now();
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("sales_order_id", salesOrderId);
        command.Parameters.AddWithValue("invoice_document_id", request.InvoiceDocumentId);
        command.Parameters.AddWithValue("invoice_display_number", request.InvoiceDisplayNumber.Trim());
        command.Parameters.AddWithValue("invoice_status", NormalizeInvoiceStatus(request.InvoiceStatus));
        command.Parameters.AddWithValue("invoice_document_date", request.DocumentDate);
        command.Parameters.AddWithValue("invoice_total_amount", request.TotalAmount);
        command.Parameters.AddWithValue("invoice_quantity", request.Quantity);
        command.Parameters.AddWithValue("lines_json", linePayload);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ShellSalesOrderInvoiceCoverageSummary?> GetSalesOrderInvoiceCoverageAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken = default)
    {
        var salesOrder = await GetAsync(companyId, salesOrderId, cancellationToken);
        if (salesOrder is null || salesOrder.DocumentType != "sales_order")
        {
            return null;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            select invoice_document_id, invoice_display_number, invoice_status, invoice_document_date,
                   invoice_total_amount, invoice_quantity, lines_json, updated_at
            from {SalesOrderInvoiceAnchorsTableName}
            where company_id = @company_id
              and sales_order_id = @sales_order_id
            order by updated_at desc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("sales_order_id", salesOrderId);

        var anchors = new List<InvoiceAnchorRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            anchors.Add(ReadInvoiceAnchor(reader));
        }

        var postedAnchors = anchors.Where(static anchor => IsPostedInvoiceStatus(anchor.InvoiceStatus)).ToArray();
        var orderQuantity = salesOrder.Lines.Sum(static line => line.Quantity);
        var invoicedQuantity = postedAnchors.Sum(static anchor => anchor.Quantity);
        var lineQuantities = postedAnchors
            .SelectMany(static anchor => anchor.Lines)
            .GroupBy(static line => BuildInvoiceCoverageLineKey(line.LineNumber, line.ItemId, line.WarehouseId, line.UomCode, line.Description))
            .ToDictionary(static group => group.Key, static group => group.Sum(line => line.Quantity));

        var lineSummaries = salesOrder.Lines
            .OrderBy(static line => line.LineNumber)
            .Select(line =>
            {
                var key = BuildInvoiceCoverageLineKey(line.LineNumber, line.ItemId, line.WarehouseId, line.UomCode, line.Description);
                var lineInvoicedQuantity = lineQuantities.TryGetValue(key, out var quantity) ? quantity : 0m;
                return new ShellSalesOrderInvoiceCoverageLineSummary
                {
                    LineNumber = line.LineNumber,
                    Description = line.Description,
                    OrderQuantity = line.Quantity,
                    InvoicedQuantity = lineInvoicedQuantity,
                    RemainingToInvoiceQuantity = Math.Max(0m, line.Quantity - lineInvoicedQuantity),
                    InvoiceCoverageStatus = BuildQuantityCoverageStatus(line.Quantity, lineInvoicedQuantity),
                    ItemId = line.ItemId,
                    WarehouseId = line.WarehouseId,
                    UomCode = line.UomCode
                };
            })
            .ToArray();

        return new ShellSalesOrderInvoiceCoverageSummary
        {
            SalesOrderId = salesOrderId,
            OrderQuantity = orderQuantity,
            InvoicedQuantity = invoicedQuantity,
            RemainingToInvoiceQuantity = Math.Max(0m, orderQuantity - invoicedQuantity),
            InvoiceCoverageStatus = BuildQuantityCoverageStatus(orderQuantity, invoicedQuantity),
            InvoiceCount = anchors.Count,
            PostedInvoiceCount = postedAnchors.Length,
            LatestInvoiceUpdatedAt = anchors.Count == 0 ? null : anchors.Max(static anchor => anchor.UpdatedAt),
            Lines = lineSummaries,
            RecentInvoices = anchors
                .Take(8)
                .Select(static anchor => new ShellSalesOrderInvoiceCoverageInvoiceSummary
                {
                    InvoiceDocumentId = anchor.InvoiceDocumentId,
                    InvoiceDisplayNumber = anchor.InvoiceDisplayNumber,
                    InvoiceStatus = anchor.InvoiceStatus,
                    DocumentDate = anchor.DocumentDate,
                    TotalAmount = anchor.TotalAmount,
                    Quantity = anchor.Quantity,
                    UpdatedAt = anchor.UpdatedAt
                })
                .ToArray()
        };
    }

    public async Task<ShellSalesOrderOutboundControlSummary?> GetSalesOrderOutboundControlAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken = default)
    {
        var salesOrder = await GetAsync(companyId, salesOrderId, cancellationToken);
        if (salesOrder is null || salesOrder.DocumentType != "sales_order")
        {
            return null;
        }

        var invoiceCoverage = await GetSalesOrderInvoiceCoverageAsync(companyId, salesOrderId, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);

        var shipmentRows = await LoadSalesOrderShipmentsAsync(connection, companyId, salesOrderId, cancellationToken);
        var shippedQuantities = await LoadSalesOrderShippedQuantitiesAsync(connection, companyId, salesOrderId, cancellationToken);
        var invoiceLines = invoiceCoverage?.Lines.ToDictionary(static line => line.LineNumber)
            ?? new Dictionary<int, ShellSalesOrderInvoiceCoverageLineSummary>();

        var shippableQuantity = salesOrder.Lines
            .Where(static line => IsInventoryShippable(line))
            .Sum(static line => line.Quantity);
        var shippedQuantity = shipmentRows.Sum(static row => row.Quantity);
        var lineSummaries = salesOrder.Lines
            .OrderBy(static line => line.LineNumber)
            .Select(line =>
            {
                invoiceLines.TryGetValue(line.LineNumber, out var invoiceLine);
                var isShippable = IsInventoryShippable(line);
                var shipmentKey = BuildInvoiceCoverageLineKey(line.LineNumber, line.ItemId, line.WarehouseId, line.UomCode, line.Description);
                var lineShippedQuantity = isShippable && shippedQuantities.TryGetValue(shipmentKey, out var quantity) ? quantity : 0m;
                return new ShellSalesOrderOutboundControlLineSummary
                {
                    LineNumber = line.LineNumber,
                    Description = line.Description,
                    OrderQuantity = line.Quantity,
                    IsInventoryShippable = isShippable,
                    ShippedQuantity = lineShippedQuantity,
                    RemainingToShipQuantity = isShippable ? Math.Max(0m, line.Quantity - lineShippedQuantity) : 0m,
                    ShipmentCoverageStatus = isShippable
                        ? BuildQuantityCoverageStatus(line.Quantity, lineShippedQuantity).Replace("invoiced", "shipped", StringComparison.Ordinal)
                        : "no_inventory_handoff",
                    InvoicedQuantity = invoiceLine?.InvoicedQuantity ?? 0m,
                    RemainingToInvoiceQuantity = invoiceLine?.RemainingToInvoiceQuantity ?? line.Quantity,
                    InvoiceCoverageStatus = invoiceLine?.InvoiceCoverageStatus ?? "not_invoiced",
                    ItemId = line.ItemId,
                    WarehouseId = line.WarehouseId,
                    UomCode = line.UomCode
                };
            })
            .ToArray();

        var shipmentCoverageStatus = shippableQuantity <= 0m
            ? "no_inventory_handoff"
            : BuildQuantityCoverageStatus(shippableQuantity, shippedQuantity).Replace("invoiced", "shipped", StringComparison.Ordinal);
        var invoiceCoverageStatus = invoiceCoverage?.InvoiceCoverageStatus ?? "not_invoiced";

        return new ShellSalesOrderOutboundControlSummary
        {
            SalesOrderId = salesOrderId,
            SalesOrderStatus = salesOrder.Status,
            AggregateStatus = BuildSalesOrderAggregateStatus(salesOrder.Status, shipmentCoverageStatus, invoiceCoverageStatus),
            OrderQuantity = salesOrder.Lines.Sum(static line => line.Quantity),
            ShippableQuantity = shippableQuantity,
            ShippedQuantity = shippedQuantity,
            RemainingToShipQuantity = Math.Max(0m, shippableQuantity - shippedQuantity),
            ShipmentCoverageStatus = shipmentCoverageStatus,
            ShipmentCount = shipmentRows.Count,
            InvoicedQuantity = invoiceCoverage?.InvoicedQuantity ?? 0m,
            RemainingToInvoiceQuantity = invoiceCoverage?.RemainingToInvoiceQuantity ?? salesOrder.Lines.Sum(static line => line.Quantity),
            InvoiceCoverageStatus = invoiceCoverageStatus,
            InvoiceCount = invoiceCoverage?.InvoiceCount ?? 0,
            PostedInvoiceCount = invoiceCoverage?.PostedInvoiceCount ?? 0,
            LatestShipmentPostedAt = shipmentRows.Count == 0 ? null : shipmentRows.Max(static row => row.PostedAt),
            LatestInvoiceUpdatedAt = invoiceCoverage?.LatestInvoiceUpdatedAt,
            Lines = lineSummaries,
            RecentShipments = shipmentRows
                .Take(8)
                .Select(static row => new ShellSalesOrderOutboundShipmentSummary
                {
                    ShipmentDocumentId = row.ShipmentDocumentId,
                    DocumentNumber = row.DocumentNumber,
                    Status = row.Status,
                    PostingDate = row.PostingDate,
                    Quantity = row.Quantity,
                    CarrierName = row.CarrierName,
                    TrackingNumber = row.TrackingNumber,
                    PostedAt = row.PostedAt
                })
                .ToArray(),
            RecentInvoices = invoiceCoverage?.RecentInvoices ?? Array.Empty<ShellSalesOrderInvoiceCoverageInvoiceSummary>()
        };
    }

    private async Task<ShellSalesCommercialDocumentReadModel> SetStatusAsync(
        Guid companyId,
        Guid documentId,
        string expectedType,
        string status,
        string timestampColumn,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureTablesAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            update {DocumentsTableName}
            set status = @status,
                {timestampColumn} = coalesce({timestampColumn}, now()),
                updated_at = now()
            where company_id = @company_id
              and id = @id
              and document_type = @document_type;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("document_type", expectedType);
        command.Parameters.AddWithValue("status", status);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            throw new InvalidOperationException("The commercial document could not be found in the active company context.");
        }

        return await GetAsync(companyId, documentId, cancellationToken)
            ?? throw new InvalidOperationException("The updated commercial document could not be reloaded.");
    }

    private static async Task EnsureTablesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            create table if not exists {DocumentsTableName} (
                id uuid primary key,
                company_id uuid not null,
                document_type text not null,
                entity_number text not null,
                display_number text not null,
                status text not null,
                customer_id uuid not null,
                document_date date not null,
                expires_on date null,
                requested_ship_date date null,
                transaction_currency_code text not null,
                base_currency_code text not null,
                memo text null,
                source_quote_id uuid null,
                lines_json jsonb not null,
                created_by_user_id uuid not null,
                created_at timestamptz not null,
                updated_at timestamptz not null,
                accepted_at timestamptz null,
                converted_at timestamptz null,
                issued_at timestamptz null,
                closed_at timestamptz null,
                constraint web_shell_sales_commercial_documents_type_chk check (document_type in ('quote', 'sales_order')),
                constraint web_shell_sales_commercial_documents_status_chk check (status in ('draft', 'accepted', 'converted', 'issued', 'closed', 'cancelled'))
            );

            alter table {DocumentsTableName}
              add column if not exists closed_at timestamptz null;

            create unique index if not exists ux_web_shell_sales_commercial_documents_company_type_entity
                on {DocumentsTableName} (company_id, document_type, entity_number);

            create index if not exists ix_web_shell_sales_commercial_documents_company_type_updated
                on {DocumentsTableName} (company_id, document_type, updated_at desc);

            create table if not exists {SequencesTableName} (
                company_id uuid not null,
                document_type text not null,
                fiscal_year integer not null,
                next_value integer not null,
                primary key (company_id, document_type, fiscal_year)
            );

            create table if not exists {SalesOrderInvoiceAnchorsTableName} (
                company_id uuid not null,
                sales_order_id uuid not null,
                invoice_document_id uuid not null,
                invoice_display_number text not null,
                invoice_status text not null,
                invoice_document_date date not null,
                invoice_total_amount numeric(18, 2) not null,
                invoice_quantity numeric(18, 6) not null,
                lines_json jsonb not null,
                updated_at timestamptz not null,
                primary key (company_id, sales_order_id, invoice_document_id)
            );

            create index if not exists ix_web_shell_sales_order_invoice_anchors_order_updated
                on {SalesOrderInvoiceAnchorsTableName} (company_id, sales_order_id, updated_at desc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        string documentType,
        int fiscalYear,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            insert into {SequencesTableName} (company_id, document_type, fiscal_year, next_value)
            values (@company_id, @document_type, @fiscal_year, 2)
            on conflict (company_id, document_type, fiscal_year)
            do update set next_value = {SequencesTableName}.next_value + 1
            returning next_value - 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("document_type", documentType);
        command.Parameters.AddWithValue("fiscal_year", fiscalYear);
        var value = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 1);
        return $"{fiscalYear}{value:000000}";
    }

    private static void BindSaveParameters(
        NpgsqlCommand command,
        ShellSalesCommercialDocumentSaveRequest request,
        string linesJson,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("company_id", request.CompanyId);
        command.Parameters.AddWithValue("customer_id", request.CustomerId);
        command.Parameters.AddWithValue("document_date", request.DocumentDate);
        command.Parameters.AddWithValue("expires_on", request.ExpiresOn.HasValue ? request.ExpiresOn.Value : DBNull.Value);
        command.Parameters.AddWithValue("requested_ship_date", request.RequestedShipDate.HasValue ? request.RequestedShipDate.Value : DBNull.Value);
        command.Parameters.AddWithValue("transaction_currency_code", NormalizeCurrency(request.TransactionCurrencyCode));
        command.Parameters.AddWithValue("base_currency_code", NormalizeCurrency(request.BaseCurrencyCode));
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(request.Memo) ? DBNull.Value : request.Memo.Trim());
        command.Parameters.AddWithValue("source_quote_id", request.SourceQuoteId.HasValue ? request.SourceQuoteId.Value : DBNull.Value);
        command.Parameters.AddWithValue("lines_json", linesJson);
        command.Parameters.AddWithValue("created_by_user_id", request.UserId);
        command.Parameters.AddWithValue("updated_at", now);
    }

    private static ShellSalesCommercialDocumentSummary ReadSummary(NpgsqlDataReader reader)
    {
        var lines = ReadLines(reader);
        return new ShellSalesCommercialDocumentSummary
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId = reader.GetGuid(reader.GetOrdinal("company_id")),
            DocumentType = reader.GetString(reader.GetOrdinal("document_type")),
            EntityNumber = reader.GetString(reader.GetOrdinal("entity_number")),
            DisplayNumber = reader.GetString(reader.GetOrdinal("display_number")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            CustomerId = reader.GetGuid(reader.GetOrdinal("customer_id")),
            DocumentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
            ExpiresOn = ReadDateOnly(reader, "expires_on"),
            RequestedShipDate = ReadDateOnly(reader, "requested_ship_date"),
            TransactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code")),
            BaseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code")),
            SourceQuoteId = reader.IsDBNull(reader.GetOrdinal("source_quote_id")) ? null : reader.GetGuid(reader.GetOrdinal("source_quote_id")),
            TotalAmount = lines.Sum(static line => line.LineAmount),
            LineCount = lines.Count,
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))
        };
    }

    private static ShellSalesCommercialDocumentReadModel ReadModel(NpgsqlDataReader reader)
    {
        var summary = ReadSummary(reader);
        return new ShellSalesCommercialDocumentReadModel
        {
            Id = summary.Id,
            CompanyId = summary.CompanyId,
            DocumentType = summary.DocumentType,
            EntityNumber = summary.EntityNumber,
            DisplayNumber = summary.DisplayNumber,
            Status = summary.Status,
            CustomerId = summary.CustomerId,
            DocumentDate = summary.DocumentDate,
            ExpiresOn = summary.ExpiresOn,
            RequestedShipDate = summary.RequestedShipDate,
            TransactionCurrencyCode = summary.TransactionCurrencyCode,
            BaseCurrencyCode = summary.BaseCurrencyCode,
            TotalAmount = summary.TotalAmount,
            LineCount = summary.LineCount,
            SourceQuoteId = summary.SourceQuoteId,
            UpdatedAt = summary.UpdatedAt,
            Memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo")),
            Lines = ReadLines(reader)
        };
    }

    private static async Task<(string DocumentType, string Status)?> GetForUpdateAsync(
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
            select document_type, status
            from {DocumentsTableName}
            where company_id = @company_id
              and id = @id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static IReadOnlyList<ShellSalesCommercialDocumentLineReadModel> ReadLines(NpgsqlDataReader reader)
    {
        var json = reader.GetString(reader.GetOrdinal("lines_json"));
        return JsonSerializer.Deserialize<IReadOnlyList<ShellSalesCommercialDocumentLineReadModel>>(json, JsonOptions)
            ?? Array.Empty<ShellSalesCommercialDocumentLineReadModel>();
    }

    private static IReadOnlyList<ShellSalesCommercialDocumentLineSaveRequest> NormalizeLines(
        IReadOnlyList<ShellSalesCommercialDocumentLineSaveRequest> lines) =>
        lines
            .OrderBy(static line => line.LineNumber)
            .Select((line, index) => line with
            {
                LineNumber = line.LineNumber <= 0 ? index + 1 : line.LineNumber,
                Description = line.Description.Trim(),
                UomCode = string.IsNullOrWhiteSpace(line.UomCode) ? null : line.UomCode.Trim().ToUpperInvariant()
            })
            .ToArray();

    private static IReadOnlyList<ShellSalesCommercialInvoiceAnchorLineRequest> NormalizeInvoiceAnchorLines(
        IReadOnlyList<ShellSalesCommercialInvoiceAnchorLineRequest> lines) =>
        lines
            .Where(static line => line.Quantity > 0m)
            .OrderBy(static line => line.LineNumber)
            .Select((line, index) => line with
            {
                LineNumber = index + 1,
                Description = line.Description.Trim(),
                UomCode = string.IsNullOrWhiteSpace(line.UomCode) ? null : line.UomCode.Trim().ToUpperInvariant()
            })
            .ToArray();

    private static InvoiceAnchorRow ReadInvoiceAnchor(NpgsqlDataReader reader)
    {
        var linesJson = reader.GetString(reader.GetOrdinal("lines_json"));
        return new InvoiceAnchorRow
        {
            InvoiceDocumentId = reader.GetGuid(reader.GetOrdinal("invoice_document_id")),
            InvoiceDisplayNumber = reader.GetString(reader.GetOrdinal("invoice_display_number")),
            InvoiceStatus = reader.GetString(reader.GetOrdinal("invoice_status")),
            DocumentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("invoice_document_date")),
            TotalAmount = reader.GetDecimal(reader.GetOrdinal("invoice_total_amount")),
            Quantity = reader.GetDecimal(reader.GetOrdinal("invoice_quantity")),
            Lines = JsonSerializer.Deserialize<IReadOnlyList<ShellSalesCommercialInvoiceAnchorLineRequest>>(linesJson, JsonOptions)
                ?? Array.Empty<ShellSalesCommercialInvoiceAnchorLineRequest>(),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))
        };
    }

    private static async Task<IReadOnlyList<ShipmentAnchorRow>> LoadSalesOrderShipmentsAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select
                  d.id,
                  coalesce(d.document_number, d.source_document_number, 'UNNUMBERED') as document_number,
                  d.status,
                  d.posting_date,
                  coalesce(sum(l.base_quantity), 0) as total_quantity,
                  d.carrier_name,
                  d.tracking_number,
                  d.posted_at
                from inventory_documents d
                left join inventory_document_lines l
                  on l.document_id = d.id
                 and l.company_id = d.company_id
                where d.company_id = @company_id
                  and d.document_type = 'shipment'
                  and d.source_module = 'sales_order'
                  and d.source_document_id = @sales_order_id
                group by
                  d.id,
                  d.document_number,
                  d.source_document_number,
                  d.status,
                  d.posting_date,
                  d.carrier_name,
                  d.tracking_number,
                  d.posted_at
                order by d.posted_at desc nulls last, d.posting_date desc, d.id desc;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("sales_order_id", salesOrderId);

            var rows = new List<ShipmentAnchorRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ShipmentAnchorRow
                {
                    ShipmentDocumentId = reader.GetGuid(reader.GetOrdinal("id")),
                    DocumentNumber = reader.GetString(reader.GetOrdinal("document_number")),
                    Status = reader.GetString(reader.GetOrdinal("status")),
                    PostingDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                    Quantity = decimal.Round(reader.GetDecimal(reader.GetOrdinal("total_quantity")), 6, MidpointRounding.AwayFromZero),
                    CarrierName = reader.IsDBNull(reader.GetOrdinal("carrier_name")) ? null : reader.GetString(reader.GetOrdinal("carrier_name")),
                    TrackingNumber = reader.IsDBNull(reader.GetOrdinal("tracking_number")) ? null : reader.GetString(reader.GetOrdinal("tracking_number")),
                    PostedAt = reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))
                });
            }

            return rows;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return Array.Empty<ShipmentAnchorRow>();
        }
    }

    private static async Task<IReadOnlyDictionary<InvoiceCoverageLineKey, decimal>> LoadSalesOrderShippedQuantitiesAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
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
                  and d.source_module = 'sales_order'
                  and d.source_document_id = @sales_order_id
                group by
                  l.item_id,
                  l.warehouse_id,
                  upper(l.uom_code);
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("sales_order_id", salesOrderId);

            var rows = new Dictionary<InvoiceCoverageLineKey, decimal>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = BuildInvoiceCoverageLineKey(
                    0,
                    reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                    reader.GetString(reader.GetOrdinal("uom_code")),
                    string.Empty);
                rows[key] = decimal.Round(reader.GetDecimal(reader.GetOrdinal("shipped_quantity")), 6, MidpointRounding.AwayFromZero);
            }

            return rows;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<InvoiceCoverageLineKey, decimal>();
        }
    }

    private static InvoiceCoverageLineKey BuildInvoiceCoverageLineKey(
        int lineNumber,
        Guid? itemId,
        Guid? warehouseId,
        string? uomCode,
        string description)
    {
        if (itemId.HasValue)
        {
            return new InvoiceCoverageLineKey(null, itemId, warehouseId, NormalizeOptionalText(uomCode), null);
        }

        return new InvoiceCoverageLineKey(lineNumber, null, null, null, NormalizeOptionalText(description));
    }

    private static string BuildQuantityCoverageStatus(decimal expectedQuantity, decimal coveredQuantity)
    {
        if (expectedQuantity <= 0m)
        {
            return "no_order_quantity";
        }

        if (coveredQuantity <= 0m)
        {
            return "not_invoiced";
        }

        if (coveredQuantity < expectedQuantity)
        {
            return "partially_invoiced";
        }

        return coveredQuantity == expectedQuantity ? "fully_invoiced" : "over_invoiced";
    }

    private static bool IsPostedInvoiceStatus(string status) =>
        string.Equals(status, "posted", StringComparison.OrdinalIgnoreCase);

    private static bool IsInventoryShippable(ShellSalesCommercialDocumentLineReadModel line) =>
        line.ItemId.HasValue &&
        line.WarehouseId.HasValue &&
        !string.IsNullOrWhiteSpace(line.UomCode);

    private static string BuildSalesOrderAggregateStatus(
        string salesOrderStatus,
        string shipmentCoverageStatus,
        string invoiceCoverageStatus)
    {
        if (string.Equals(salesOrderStatus, "draft", StringComparison.OrdinalIgnoreCase))
        {
            return "draft";
        }

        if (string.Equals(salesOrderStatus, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return "closed";
        }

        if (string.Equals(shipmentCoverageStatus, "over_shipped", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(invoiceCoverageStatus, "over_invoiced", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(shipmentCoverageStatus, "over_shipped", StringComparison.OrdinalIgnoreCase)
                ? "over_shipped"
                : "over_invoiced";
        }

        var shipmentComplete = string.Equals(shipmentCoverageStatus, "fully_shipped", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shipmentCoverageStatus, "no_inventory_handoff", StringComparison.OrdinalIgnoreCase);
        var invoiceComplete = string.Equals(invoiceCoverageStatus, "fully_invoiced", StringComparison.OrdinalIgnoreCase);
        if (shipmentComplete && invoiceComplete)
        {
            return "closed";
        }

        if (string.Equals(shipmentCoverageStatus, "partially_shipped", StringComparison.OrdinalIgnoreCase))
        {
            return "partially_shipped";
        }

        if (string.Equals(invoiceCoverageStatus, "partially_invoiced", StringComparison.OrdinalIgnoreCase))
        {
            return "partially_invoiced";
        }

        return "issued";
    }

    private static string NormalizeInvoiceStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "draft" : status.Trim().ToLowerInvariant();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static DateOnly? ReadDateOnly(NpgsqlDataReader reader, string columnName) =>
        reader.IsDBNull(reader.GetOrdinal(columnName))
            ? null
            : reader.GetFieldValue<DateOnly>(reader.GetOrdinal(columnName));

    private static void ValidateInvoiceAnchor(Guid companyId, Guid salesOrderId, ShellSalesCommercialInvoiceAnchorRequest request)
    {
        if (companyId == Guid.Empty || salesOrderId == Guid.Empty || request.InvoiceDocumentId == Guid.Empty)
        {
            throw new InvalidOperationException("Company, sales order, and invoice document are required.");
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceDisplayNumber))
        {
            throw new InvalidOperationException("Invoice display number is required.");
        }

        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one invoice anchor line is required.");
        }
    }

    private static void ValidateSave(ShellSalesCommercialDocumentSaveRequest request)
    {
        if (request.CompanyId == Guid.Empty || request.UserId == Guid.Empty || request.CustomerId == Guid.Empty)
        {
            throw new InvalidOperationException("Company, user, and customer are required.");
        }

        _ = NormalizeDocumentType(request.DocumentType);
        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("At least one commercial document line is required.");
        }

        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0m || line.UnitPrice < 0m)
            {
                throw new InvalidOperationException($"Line {line.LineNumber} must have positive quantity and non-negative price.");
            }

            if (string.IsNullOrWhiteSpace(line.Description))
            {
                throw new InvalidOperationException($"Line {line.LineNumber} needs a description.");
            }
        }
    }

    private static string NormalizeDocumentType(string? documentType) =>
        documentType?.Trim().ToLowerInvariant() switch
        {
            "quote" => "quote",
            "sales_order" => "sales_order",
            _ => throw new InvalidOperationException("Document type must be quote or sales_order.")
        };

    private static string GetDocumentPrefix(string documentType) =>
        documentType == "quote" ? "QT" : "SO";

    private static string NormalizeCurrency(string currencyCode) =>
        string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode.Trim().ToUpperInvariant();

    private sealed record class InvoiceAnchorRow
    {
        public Guid InvoiceDocumentId { get; init; }

        public string InvoiceDisplayNumber { get; init; } = string.Empty;

        public string InvoiceStatus { get; init; } = string.Empty;

        public DateOnly DocumentDate { get; init; }

        public decimal TotalAmount { get; init; }

        public decimal Quantity { get; init; }

        public IReadOnlyList<ShellSalesCommercialInvoiceAnchorLineRequest> Lines { get; init; } = Array.Empty<ShellSalesCommercialInvoiceAnchorLineRequest>();

        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed record class ShipmentAnchorRow
    {
        public Guid ShipmentDocumentId { get; init; }

        public string DocumentNumber { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateOnly PostingDate { get; init; }

        public decimal Quantity { get; init; }

        public string? CarrierName { get; init; }

        public string? TrackingNumber { get; init; }

        public DateTimeOffset? PostedAt { get; init; }
    }

    private readonly record struct InvoiceCoverageLineKey(
        int? LineNumber,
        Guid? ItemId,
        Guid? WarehouseId,
        string? UomCode,
        string? Description);
}
