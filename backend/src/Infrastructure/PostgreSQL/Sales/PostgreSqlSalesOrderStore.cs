using Citus.Accounting.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Sales;

/// <summary>
/// PostgreSQL backing for <see cref="ISalesOrderStore"/>. Owns the
/// <c>sales_orders</c> + <c>sales_order_lines</c> tables. Mirror of
/// <see cref="PostgreSqlQuoteStore"/> minus the expiration_date /
/// converted_sales_order_id columns; gains source_quote_id (back-pointer
/// to the originating quote, when converted) and invoice_number (free
/// text the user fills when marking the SO as invoiced).
/// </summary>
public sealed class PostgreSqlSalesOrderStore(PostgreSqlConnectionFactory connections) : ISalesOrderStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sales_orders (
                id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id                  UUID NOT NULL,
                sales_order_number          TEXT NOT NULL,
                status                      TEXT NOT NULL DEFAULT 'open',
                customer_id                 UUID NOT NULL,
                document_date               DATE NOT NULL,
                transaction_currency_code   CHAR(3) NOT NULL,
                fx_rate                     NUMERIC(18,8) NULL,
                billing_address_line        TEXT NULL,
                billing_city                TEXT NULL,
                billing_province_state      TEXT NULL,
                billing_postal_code         TEXT NULL,
                billing_country             TEXT NULL,
                shipping_address_line       TEXT NULL,
                shipping_city               TEXT NULL,
                shipping_province_state     TEXT NULL,
                shipping_postal_code        TEXT NULL,
                shipping_country            TEXT NULL,
                ship_via                    TEXT NULL,
                shipping_date               DATE NULL,
                tracking_no                 TEXT NULL,
                tax_mode                    TEXT NOT NULL DEFAULT 'exclusive',
                discount_kind               TEXT NULL,
                discount_value              NUMERIC(18,4) NULL,
                shipping_amount             NUMERIC(18,4) NULL,
                shipping_tax_code_id        UUID NULL,
                subtotal_amount             NUMERIC(18,4) NOT NULL DEFAULT 0,
                discount_amount             NUMERIC(18,4) NOT NULL DEFAULT 0,
                tax_amount                  NUMERIC(18,4) NOT NULL DEFAULT 0,
                total_amount                NUMERIC(18,4) NOT NULL DEFAULT 0,
                memo_to_customer            TEXT NULL,
                internal_note               TEXT NULL,
                source_quote_id             UUID NULL,
                invoice_number              TEXT NULL,
                customer_po_number          TEXT NULL,
                confirmed_at                TIMESTAMPTZ NULL,
                created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE sales_orders ADD COLUMN IF NOT EXISTS customer_po_number TEXT NULL;
            ALTER TABLE sales_orders ADD COLUMN IF NOT EXISTS confirmed_at TIMESTAMPTZ NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS uq_sales_orders_company_so_number
                ON sales_orders (company_id, sales_order_number);
            CREATE INDEX IF NOT EXISTS idx_sales_orders_company_status
                ON sales_orders (company_id, status);
            CREATE INDEX IF NOT EXISTS idx_sales_orders_company_customer
                ON sales_orders (company_id, customer_id);
            CREATE INDEX IF NOT EXISTS idx_sales_orders_company_document_date
                ON sales_orders (company_id, document_date DESC);
            CREATE INDEX IF NOT EXISTS idx_sales_orders_company_customer_po
                ON sales_orders (company_id, customer_po_number)
                WHERE customer_po_number IS NOT NULL;

            CREATE TABLE IF NOT EXISTS sales_order_lines (
                id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                sales_order_id  UUID NOT NULL REFERENCES sales_orders(id) ON DELETE CASCADE,
                sequence        INTEGER NOT NULL,
                service_date    DATE NULL,
                item_id         UUID NULL,
                description     TEXT NOT NULL DEFAULT '',
                quantity        NUMERIC(18,4) NOT NULL DEFAULT 0,
                unit_price      NUMERIC(18,4) NOT NULL DEFAULT 0,
                tax_code_id     UUID NULL,
                account_code    TEXT NULL,
                line_total      NUMERIC(18,4) NOT NULL DEFAULT 0,
                reserved_qty    NUMERIC(18,4) NOT NULL DEFAULT 0,
                backorder_qty   NUMERIC(18,4) NOT NULL DEFAULT 0,
                shipped_qty     NUMERIC(18,4) NOT NULL DEFAULT 0
            );
            ALTER TABLE sales_order_lines ADD COLUMN IF NOT EXISTS reserved_qty  NUMERIC(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE sales_order_lines ADD COLUMN IF NOT EXISTS backorder_qty NUMERIC(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE sales_order_lines ADD COLUMN IF NOT EXISTS shipped_qty   NUMERIC(18,4) NOT NULL DEFAULT 0;
            CREATE INDEX IF NOT EXISTS idx_sales_order_lines_so
                ON sales_order_lines (sales_order_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SalesOrderSummary>> ListAsync(
        Guid companyId,
        SalesOrderListFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = new List<SalesOrderSummary>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sql = """
            SELECT s.id, s.company_id, s.sales_order_number, s.customer_id,
                   COALESCE(c.display_name, '') AS customer_name,
                   s.document_date, s.status,
                   s.transaction_currency_code, s.total_amount,
                   s.source_quote_id, s.invoice_number,
                   s.customer_po_number,
                   s.confirmed_at,
                   s.created_at, s.updated_at
              FROM sales_orders s
              LEFT JOIN customers c ON c.id = s.customer_id
             WHERE s.company_id = @company_id
            """;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            sql += " AND s.status = @status";
            command.Parameters.AddWithValue("status", filter.Status);
        }
        if (filter.CustomerId is { } customerId)
        {
            sql += " AND s.customer_id = @customer_id";
            command.Parameters.AddWithValue("customer_id", customerId);
        }
        if (filter.FromDate is { } fromDate)
        {
            sql += " AND s.document_date >= @from_date";
            command.Parameters.AddWithValue("from_date", fromDate);
        }
        if (filter.ToDate is { } toDate)
        {
            sql += " AND s.document_date <= @to_date";
            command.Parameters.AddWithValue("to_date", toDate);
        }
        sql += " ORDER BY s.document_date DESC, s.created_at DESC;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapSummary(reader));
        }
        return rows;
    }

    public async Task<SalesOrderRecord?> GetByIdAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        SalesOrderRecord? so;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectSalesOrderColumns + " WHERE s.company_id = @company_id AND s.id = @id LIMIT 1;";
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("id", salesOrderId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            so = MapRecord(reader, lines: Array.Empty<SalesOrderLineRecord>());
        }

        var lines = await ReadLinesAsync(connection, salesOrderId, cancellationToken).ConfigureAwait(false);
        return so with { Lines = lines };
    }

    public async Task<SalesOrderRecord> CreateAsync(
        Guid companyId,
        SalesOrderUpsertInput input,
        CancellationToken cancellationToken)
    {
        var (subtotal, discount, tax, total) = ComputeTotals(input);
        var soNumber = GenerateSalesOrderNumber();

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid soId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sales_orders (
                    company_id, sales_order_number, status, customer_id,
                    document_date,
                    transaction_currency_code, fx_rate,
                    billing_address_line, billing_city, billing_province_state, billing_postal_code, billing_country,
                    shipping_address_line, shipping_city, shipping_province_state, shipping_postal_code, shipping_country,
                    ship_via, shipping_date, tracking_no,
                    tax_mode, discount_kind, discount_value, shipping_amount, shipping_tax_code_id,
                    subtotal_amount, discount_amount, tax_amount, total_amount,
                    memo_to_customer, internal_note, source_quote_id, customer_po_number
                )
                VALUES (
                    @company_id, @sales_order_number, 'open', @customer_id,
                    @document_date,
                    @transaction_currency_code, @fx_rate,
                    @billing_address_line, @billing_city, @billing_province_state, @billing_postal_code, @billing_country,
                    @shipping_address_line, @shipping_city, @shipping_province_state, @shipping_postal_code, @shipping_country,
                    @ship_via, @shipping_date, @tracking_no,
                    @tax_mode, @discount_kind, @discount_value, @shipping_amount, @shipping_tax_code_id,
                    @subtotal_amount, @discount_amount, @tax_amount, @total_amount,
                    @memo_to_customer, @internal_note, @source_quote_id, @customer_po_number
                )
                RETURNING id;
                """;
            BindUpsertParameters(command, companyId, input, subtotal, discount, tax, total);
            command.Parameters.AddWithValue("sales_order_number", soNumber);
            soId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await InsertLinesAsync(connection, transaction, soId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var saved = await GetByIdAsync(companyId, soId, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException("Sales order insert returned no row.");
    }

    public async Task<SalesOrderRecord?> UpdateAsync(
        Guid companyId,
        Guid salesOrderId,
        SalesOrderUpsertInput input,
        CancellationToken cancellationToken)
    {
        var (subtotal, discount, tax, total) = ComputeTotals(input);

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var statusCommand = connection.CreateCommand())
        {
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = "SELECT status FROM sales_orders WHERE company_id = @company_id AND id = @id LIMIT 1;";
            statusCommand.Parameters.AddWithValue("company_id", companyId);
            statusCommand.Parameters.AddWithValue("id", salesOrderId);
            var statusObj = await statusCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null) return null;
            var currentStatus = (string)statusObj;
            if (!SalesOrderStatus.IsEditable(currentStatus))
            {
                throw new InvalidOperationException(
                    $"Sales order in status '{currentStatus}' cannot be edited.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sales_orders
                   SET customer_id              = @customer_id,
                       document_date            = @document_date,
                       transaction_currency_code = @transaction_currency_code,
                       fx_rate                  = @fx_rate,
                       billing_address_line     = @billing_address_line,
                       billing_city             = @billing_city,
                       billing_province_state   = @billing_province_state,
                       billing_postal_code      = @billing_postal_code,
                       billing_country          = @billing_country,
                       shipping_address_line    = @shipping_address_line,
                       shipping_city            = @shipping_city,
                       shipping_province_state  = @shipping_province_state,
                       shipping_postal_code     = @shipping_postal_code,
                       shipping_country         = @shipping_country,
                       ship_via                 = @ship_via,
                       shipping_date            = @shipping_date,
                       tracking_no              = @tracking_no,
                       tax_mode                 = @tax_mode,
                       discount_kind            = @discount_kind,
                       discount_value           = @discount_value,
                       shipping_amount          = @shipping_amount,
                       shipping_tax_code_id     = @shipping_tax_code_id,
                       subtotal_amount          = @subtotal_amount,
                       discount_amount          = @discount_amount,
                       tax_amount               = @tax_amount,
                       total_amount             = @total_amount,
                       memo_to_customer         = @memo_to_customer,
                       internal_note            = @internal_note,
                       customer_po_number       = @customer_po_number,
                       updated_at               = NOW()
                 WHERE company_id = @company_id AND id = @id;
                """;
            BindUpsertParameters(command, companyId, input, subtotal, discount, tax, total);
            command.Parameters.AddWithValue("id", salesOrderId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteLines = connection.CreateCommand())
        {
            deleteLines.Transaction = transaction;
            deleteLines.CommandText = "DELETE FROM sales_order_lines WHERE sales_order_id = @id;";
            deleteLines.Parameters.AddWithValue("id", salesOrderId);
            await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertLinesAsync(connection, transaction, salesOrderId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, salesOrderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SalesOrderRecord?> MarkInvoicedAsync(
        Guid companyId,
        Guid salesOrderId,
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sales_orders
               SET status         = 'invoiced',
                   invoice_number = @invoice_number,
                   updated_at     = NOW()
             WHERE company_id = @company_id AND id = @id
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", salesOrderId);
        command.Parameters.AddWithValue("invoice_number", invoiceNumber.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return null;

        return await GetByIdAsync(companyId, salesOrderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SalesOrderRecord?> ConfirmAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Status guard — only Open SOs can confirm. Re-confirming a Confirmed
        // SO would double-bump reservations; this guard makes that impossible.
        await using (var statusCommand = connection.CreateCommand())
        {
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = "SELECT status FROM sales_orders WHERE company_id = @company_id AND id = @id LIMIT 1;";
            statusCommand.Parameters.AddWithValue("company_id", companyId);
            statusCommand.Parameters.AddWithValue("id", salesOrderId);
            var statusObj = await statusCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null) return null;
            var currentStatus = (string)statusObj;
            if (!SalesOrderStatus.CanConfirm(currentStatus))
            {
                throw new InvalidOperationException(
                    $"Sales order in status '{currentStatus}' cannot be confirmed. Only Open SOs can confirm.");
            }
        }

        // Resolve the company's single warehouse (V1 assumption). If no
        // warehouse exists yet (Inventory module not activated, or company
        // hasn't created one), confirm still flips status but skips
        // reservation logic — every line is treated as service-style.
        Guid? warehouseId = null;
        await using (var warehouseCommand = connection.CreateCommand())
        {
            warehouseCommand.Transaction = transaction;
            warehouseCommand.CommandText = """
                SELECT id FROM inventory_warehouses
                 WHERE company_id = @company_id AND is_active = true
                 ORDER BY created_at
                 LIMIT 1;
                """;
            warehouseCommand.Parameters.AddWithValue("company_id", companyId);
            var result = await warehouseCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is Guid id) warehouseId = id;
        }

        // Read all lines + their item kind + backorder mode in one shot. Lines
        // with no inventory item (free-form description) are service-style and
        // get reserved=0/backorder=0 unconditionally.
        var reservationLines = new List<ReservationLine>();
        await using (var linesCommand = connection.CreateCommand())
        {
            linesCommand.Transaction = transaction;
            linesCommand.CommandText = """
                SELECT sol.id AS line_id,
                       sol.item_id,
                       sol.quantity,
                       ii.item_kind,
                       ii.backorder_mode,
                       COALESCE(ii.name, sol.description) AS item_label
                  FROM sales_order_lines sol
                  LEFT JOIN inventory_items ii
                    ON ii.company_id = @company_id AND ii.id = sol.item_id
                 WHERE sol.sales_order_id = @so_id
                 ORDER BY sol.sequence;
                """;
            linesCommand.Parameters.AddWithValue("company_id", companyId);
            linesCommand.Parameters.AddWithValue("so_id", salesOrderId);
            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reservationLines.Add(new ReservationLine(
                    LineId: reader.GetGuid(0),
                    ItemId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    Quantity: reader.GetDecimal(2),
                    ItemKind: reader.IsDBNull(3) ? null : reader.GetString(3),
                    BackorderMode: reader.IsDBNull(4) ? null : reader.GetString(4),
                    ItemLabel: reader.GetString(5)));
            }
        }

        // Per-line split: only Stock-kind items with a warehouse get reserved.
        var splits = new List<ReservationSplit>(reservationLines.Count);
        foreach (var line in reservationLines)
        {
            // Service / NonStock / no-item lines: skip reservation entirely.
            if (warehouseId is null
                || line.ItemId is null
                || !string.Equals(line.ItemKind, "stock", StringComparison.OrdinalIgnoreCase))
            {
                splits.Add(new ReservationSplit(line.LineId, ItemId: null, Reserved: 0m, Backorder: 0m));
                continue;
            }

            // available = max(0, on_hand - reserved). Missing balance row = 0.
            decimal available = 0m;
            await using (var balanceCommand = connection.CreateCommand())
            {
                balanceCommand.Transaction = transaction;
                balanceCommand.CommandText = """
                    SELECT GREATEST(on_hand_qty - reserved_qty, 0) AS available_qty
                      FROM item_warehouse_balances
                     WHERE company_id = @company_id AND item_id = @item_id AND warehouse_id = @warehouse_id
                     LIMIT 1;
                    """;
                balanceCommand.Parameters.AddWithValue("company_id", companyId);
                balanceCommand.Parameters.AddWithValue("item_id", line.ItemId.Value);
                balanceCommand.Parameters.AddWithValue("warehouse_id", warehouseId.Value);
                var result = await balanceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (result is decimal d) available = d;
            }

            var reserved = Math.Min(line.Quantity, available);
            var backorder = line.Quantity - reserved;

            if (backorder > 0m
                && string.Equals(line.BackorderMode, "disallow", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot confirm: insufficient stock for '{line.ItemLabel}' (need {line.Quantity:0.##}, " +
                    $"available {available:0.##}) and item is set to disallow backorder.");
            }

            splits.Add(new ReservationSplit(line.LineId, line.ItemId, reserved, backorder));
        }

        // Apply per-line counter updates.
        foreach (var split in splits)
        {
            await using var updateLine = connection.CreateCommand();
            updateLine.Transaction = transaction;
            updateLine.CommandText = """
                UPDATE sales_order_lines
                   SET reserved_qty  = @reserved_qty,
                       backorder_qty = @backorder_qty
                 WHERE id = @line_id;
                """;
            updateLine.Parameters.AddWithValue("line_id", split.LineId);
            updateLine.Parameters.AddWithValue("reserved_qty", split.Reserved);
            updateLine.Parameters.AddWithValue("backorder_qty", split.Backorder);
            await updateLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Aggregate reservations by item (multiple lines can share one item)
        // and bump the warehouse balance once per item.
        if (warehouseId is { } whId)
        {
            var perItemReserved = splits
                .Where(s => s.ItemId is not null && s.Reserved > 0m)
                .GroupBy(s => s.ItemId!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Reserved));

            foreach (var (itemId, reservedDelta) in perItemReserved)
            {
                await using var upsertBalance = connection.CreateCommand();
                upsertBalance.Transaction = transaction;
                // ON CONFLICT targets the (company_id, item_id, warehouse_id)
                // unique index ux_item_warehouse_balances_company_item_warehouse.
                upsertBalance.CommandText = """
                    INSERT INTO item_warehouse_balances
                        (company_id, item_id, warehouse_id, on_hand_qty, reserved_qty,
                         in_transit_out_qty, in_transit_in_qty, updated_at)
                    VALUES
                        (@company_id, @item_id, @warehouse_id, 0, @reserved_delta,
                         0, 0, now())
                    ON CONFLICT (company_id, item_id, warehouse_id)
                    DO UPDATE SET
                        reserved_qty = item_warehouse_balances.reserved_qty + EXCLUDED.reserved_qty,
                        updated_at = now();
                    """;
                upsertBalance.Parameters.AddWithValue("company_id", companyId);
                upsertBalance.Parameters.AddWithValue("item_id", itemId);
                upsertBalance.Parameters.AddWithValue("warehouse_id", whId);
                upsertBalance.Parameters.AddWithValue("reserved_delta", reservedDelta);
                await upsertBalance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Status flip + confirmed_at stamp.
        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText = """
                UPDATE sales_orders
                   SET status       = 'confirmed',
                       confirmed_at = now(),
                       updated_at   = now()
                 WHERE company_id = @company_id AND id = @id;
                """;
            headerCommand.Parameters.AddWithValue("company_id", companyId);
            headerCommand.Parameters.AddWithValue("id", salesOrderId);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, salesOrderId, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ReservationLine(
        Guid LineId,
        Guid? ItemId,
        decimal Quantity,
        string? ItemKind,
        string? BackorderMode,
        string ItemLabel);

    private sealed record ReservationSplit(
        Guid LineId,
        Guid? ItemId,
        decimal Reserved,
        decimal Backorder);

    public async Task<SalesOrderCancelResult?> CancelAsync(
        Guid companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Status guard — only Open / Confirmed SOs are cancellable. Invoiced
        // / Cancelled / future statuses stay terminal.
        string currentStatus;
        await using (var statusCommand = connection.CreateCommand())
        {
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = "SELECT status FROM sales_orders WHERE company_id = @company_id AND id = @id LIMIT 1;";
            statusCommand.Parameters.AddWithValue("company_id", companyId);
            statusCommand.Parameters.AddWithValue("id", salesOrderId);
            var statusObj = await statusCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null) return null;
            currentStatus = (string)statusObj;
            if (currentStatus is not (SalesOrderStatus.Open or SalesOrderStatus.Confirmed))
            {
                throw new InvalidOperationException(
                    $"Sales order in status '{currentStatus}' cannot be cancelled. " +
                    "Only Open and Confirmed SOs are cancellable.");
            }
        }

        // Confirmed SOs may carry reservations to release. Open SOs have
        // counters at 0 by definition (no Confirm has run yet) so the
        // release loop below is a no-op for them — single code path keeps
        // future "auto-confirm on create" experiments safe.
        Guid? warehouseId = null;
        await using (var warehouseCommand = connection.CreateCommand())
        {
            warehouseCommand.Transaction = transaction;
            warehouseCommand.CommandText = """
                SELECT id FROM inventory_warehouses
                 WHERE company_id = @company_id AND is_active = true
                 ORDER BY created_at
                 LIMIT 1;
                """;
            warehouseCommand.Parameters.AddWithValue("company_id", companyId);
            var result = await warehouseCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is Guid id) warehouseId = id;
        }

        // Per-item aggregation — a single SO can carry multiple lines for
        // the same item; we want one balance UPDATE per item rather than
        // N (avoids stomp-then-restomp on the trigger from M5 iter 2).
        var perItemRelease = new Dictionary<Guid, decimal>();
        await using (var linesCommand = connection.CreateCommand())
        {
            linesCommand.Transaction = transaction;
            linesCommand.CommandText = """
                SELECT item_id, reserved_qty
                  FROM sales_order_lines
                 WHERE sales_order_id = @so_id
                   AND item_id IS NOT NULL
                   AND reserved_qty > 0;
                """;
            linesCommand.Parameters.AddWithValue("so_id", salesOrderId);
            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var itemId = reader.GetGuid(0);
                var reservedQty = reader.GetDecimal(1);
                perItemRelease[itemId] = perItemRelease.TryGetValue(itemId, out var existing)
                    ? existing + reservedQty
                    : reservedQty;
            }
        }

        // Decrement warehouse reservations. GREATEST(...,0) guards against
        // any drift between SO line counters and the balance row — if the
        // balance somehow under-counts (data corruption / partial repair),
        // we don't push it negative.
        if (warehouseId is { } whId && perItemRelease.Count > 0)
        {
            foreach (var (itemId, reservedDelta) in perItemRelease)
            {
                await using var updateBalance = connection.CreateCommand();
                updateBalance.Transaction = transaction;
                updateBalance.CommandText = """
                    UPDATE item_warehouse_balances
                       SET reserved_qty = GREATEST(reserved_qty - @reserved_delta, 0),
                           updated_at = now()
                     WHERE company_id = @company_id
                       AND item_id = @item_id
                       AND warehouse_id = @warehouse_id;
                    """;
                updateBalance.Parameters.AddWithValue("company_id", companyId);
                updateBalance.Parameters.AddWithValue("item_id", itemId);
                updateBalance.Parameters.AddWithValue("warehouse_id", whId);
                updateBalance.Parameters.AddWithValue("reserved_delta", reservedDelta);
                await updateBalance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Zero the per-line counters in one statement.
        await using (var clearCounters = connection.CreateCommand())
        {
            clearCounters.Transaction = transaction;
            clearCounters.CommandText = """
                UPDATE sales_order_lines
                   SET reserved_qty  = 0,
                       backorder_qty = 0
                 WHERE sales_order_id = @so_id
                   AND (reserved_qty > 0 OR backorder_qty > 0);
                """;
            clearCounters.Parameters.AddWithValue("so_id", salesOrderId);
            await clearCounters.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Flip status.
        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText = """
                UPDATE sales_orders
                   SET status = 'cancelled',
                       updated_at = now()
                 WHERE company_id = @company_id AND id = @id;
                """;
            headerCommand.Parameters.AddWithValue("company_id", companyId);
            headerCommand.Parameters.AddWithValue("id", salesOrderId);
            await headerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Look up open customer deposits for this SO (V1: not auto-refunded;
        // operator handles via Refund Receipt or re-applies elsewhere).
        var depositCount = 0;
        var depositTotalBase = 0m;
        await using (var depositsCommand = connection.CreateCommand())
        {
            depositsCommand.Transaction = transaction;
            depositsCommand.CommandText = """
                SELECT count(*)::int, COALESCE(SUM(oi.open_amount_base), 0)
                  FROM customer_deposits cd
                  JOIN ar_open_items oi
                    ON oi.company_id = cd.company_id
                   AND oi.source_type = 'customer_deposit'
                   AND oi.source_id = cd.id
                 WHERE cd.company_id = @company_id
                   AND cd.source_sales_order_id = @so_id
                   AND cd.status IN ('open', 'partially_applied')
                   AND oi.open_amount_base > 0;
                """;
            depositsCommand.Parameters.AddWithValue("company_id", companyId);
            depositsCommand.Parameters.AddWithValue("so_id", salesOrderId);
            await using var reader = await depositsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                depositCount = reader.GetInt32(0);
                depositTotalBase = reader.GetDecimal(1);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var saved = await GetByIdAsync(companyId, salesOrderId, cancellationToken).ConfigureAwait(false);
        if (saved is null) return null;

        return new SalesOrderCancelResult(
            SalesOrder: saved,
            OpenDepositSummary: new SalesOrderCancelDepositSummary(
                OpenDepositCount: depositCount,
                TotalOpenAmountBase: depositTotalBase));
    }

    public async Task<SalesOrderRecord?> SetStatusAsync(
        Guid companyId,
        Guid salesOrderId,
        string newStatus,
        CancellationToken cancellationToken)
    {
        if (!SalesOrderStatus.IsValid(newStatus))
        {
            throw new InvalidOperationException($"Invalid status '{newStatus}'.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sales_orders
               SET status     = @new_status,
                   updated_at = NOW()
             WHERE company_id = @company_id AND id = @id
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("id", salesOrderId);
        command.Parameters.AddWithValue("new_status", newStatus);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return null;

        return await GetByIdAsync(companyId, salesOrderId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SalesOrderLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        var lines = new List<SalesOrderLineRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, sales_order_id, sequence, service_date, item_id, description,
                   quantity, unit_price, tax_code_id, account_code, line_total,
                   reserved_qty, backorder_qty, shipped_qty
              FROM sales_order_lines
             WHERE sales_order_id = @so_id
             ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("so_id", salesOrderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new SalesOrderLineRecord(
                Id: reader.GetGuid(0),
                SalesOrderId: reader.GetGuid(1),
                Sequence: reader.GetInt32(2),
                ServiceDate: reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                ItemId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                Description: reader.GetString(5),
                Quantity: reader.GetDecimal(6),
                UnitPrice: reader.GetDecimal(7),
                TaxCodeId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                AccountCode: reader.IsDBNull(9) ? null : reader.GetString(9),
                LineTotal: reader.GetDecimal(10),
                ReservedQty: reader.GetDecimal(11),
                BackorderQty: reader.GetDecimal(12),
                ShippedQty: reader.GetDecimal(13)));
        }
        return lines;
    }

    private static async Task InsertLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid salesOrderId,
        IReadOnlyList<SalesOrderLineInput> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sales_order_lines (
                    sales_order_id, sequence, service_date, item_id, description,
                    quantity, unit_price, tax_code_id, account_code, line_total)
                VALUES (
                    @so_id, @sequence, @service_date, @item_id, @description,
                    @quantity, @unit_price, @tax_code_id, @account_code, @line_total);
                """;
            command.Parameters.AddWithValue("so_id", salesOrderId);
            command.Parameters.AddWithValue("sequence", line.Sequence);
            command.Parameters.Add("service_date", NpgsqlDbType.Date).Value =
                line.ServiceDate is { } svc ? svc.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
            command.Parameters.AddWithValue("item_id", (object?)line.ItemId ?? DBNull.Value);
            command.Parameters.AddWithValue("description", line.Description ?? string.Empty);
            command.Parameters.AddWithValue("quantity", line.Quantity);
            command.Parameters.AddWithValue("unit_price", line.UnitPrice);
            command.Parameters.AddWithValue("tax_code_id", (object?)line.TaxCodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("account_code", (object?)line.AccountCode ?? DBNull.Value);
            command.Parameters.AddWithValue("line_total", Math.Round(line.Quantity * line.UnitPrice, 4));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static (decimal subtotal, decimal discount, decimal tax, decimal total) ComputeTotals(SalesOrderUpsertInput input)
    {
        decimal subtotal = 0m;
        foreach (var line in input.Lines)
        {
            subtotal += Math.Round(line.Quantity * line.UnitPrice, 4);
        }

        decimal discount = 0m;
        if (string.Equals(input.DiscountKind, "percent", StringComparison.OrdinalIgnoreCase) && input.DiscountValue is { } pct)
        {
            discount = Math.Round(subtotal * pct / 100m, 4);
        }
        else if (string.Equals(input.DiscountKind, "amount", StringComparison.OrdinalIgnoreCase) && input.DiscountValue is { } amt)
        {
            discount = Math.Round(amt, 4);
        }

        decimal tax = 0m;
        decimal shipping = Math.Round(input.ShippingAmount ?? 0m, 4);

        decimal total = string.Equals(input.TaxMode, "inclusive", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(subtotal - discount + shipping, 4)
            : Math.Round(subtotal - discount + tax + shipping, 4);

        return (subtotal, discount, tax, total);
    }

    private static void BindUpsertParameters(
        NpgsqlCommand command,
        Guid companyId,
        SalesOrderUpsertInput input,
        decimal subtotal,
        decimal discount,
        decimal tax,
        decimal total)
    {
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("customer_id", input.CustomerId);
        command.Parameters.Add("document_date", NpgsqlDbType.Date).Value = input.DocumentDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.AddWithValue("transaction_currency_code", input.TransactionCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("fx_rate", (object?)input.FxRate ?? DBNull.Value);
        command.Parameters.AddWithValue("billing_address_line", (object?)input.BillingAddressLine ?? DBNull.Value);
        command.Parameters.AddWithValue("billing_city", (object?)input.BillingCity ?? DBNull.Value);
        command.Parameters.AddWithValue("billing_province_state", (object?)input.BillingProvinceState ?? DBNull.Value);
        command.Parameters.AddWithValue("billing_postal_code", (object?)input.BillingPostalCode ?? DBNull.Value);
        command.Parameters.AddWithValue("billing_country", (object?)input.BillingCountry ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_address_line", (object?)input.ShippingAddressLine ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_city", (object?)input.ShippingCity ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_province_state", (object?)input.ShippingProvinceState ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_postal_code", (object?)input.ShippingPostalCode ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_country", (object?)input.ShippingCountry ?? DBNull.Value);
        command.Parameters.AddWithValue("ship_via", (object?)input.ShipVia ?? DBNull.Value);
        command.Parameters.Add("shipping_date", NpgsqlDbType.Date).Value =
            input.ShippingDate is { } shipDate ? shipDate.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
        command.Parameters.AddWithValue("tracking_no", (object?)input.TrackingNo ?? DBNull.Value);
        command.Parameters.AddWithValue("tax_mode", input.TaxMode);
        command.Parameters.AddWithValue("discount_kind", (object?)input.DiscountKind ?? DBNull.Value);
        command.Parameters.AddWithValue("discount_value", (object?)input.DiscountValue ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_amount", (object?)input.ShippingAmount ?? DBNull.Value);
        command.Parameters.AddWithValue("shipping_tax_code_id", (object?)input.ShippingTaxCodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("subtotal_amount", subtotal);
        command.Parameters.AddWithValue("discount_amount", discount);
        command.Parameters.AddWithValue("tax_amount", tax);
        command.Parameters.AddWithValue("total_amount", total);
        command.Parameters.AddWithValue("memo_to_customer", (object?)input.MemoToCustomer ?? DBNull.Value);
        command.Parameters.AddWithValue("internal_note", (object?)input.InternalNote ?? DBNull.Value);
        command.Parameters.AddWithValue("source_quote_id", (object?)input.SourceQuoteId ?? DBNull.Value);
        command.Parameters.AddWithValue("customer_po_number", (object?)input.CustomerPoNumber ?? DBNull.Value);
    }

    /// <summary>
    /// SO{4-digit-year}{8-digit-random}.
    /// </summary>
    private static string GenerateSalesOrderNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, 100_000_000);
        return $"SO{year:0000}{seed:00000000}";
    }

    private const string SelectSalesOrderColumns = """
        SELECT s.id, s.company_id, s.sales_order_number, s.status, s.customer_id,
               COALESCE(c.display_name, '') AS customer_name,
               s.document_date,
               s.transaction_currency_code, s.fx_rate,
               s.billing_address_line, s.billing_city, s.billing_province_state, s.billing_postal_code, s.billing_country,
               s.shipping_address_line, s.shipping_city, s.shipping_province_state, s.shipping_postal_code, s.shipping_country,
               s.ship_via, s.shipping_date, s.tracking_no,
               s.tax_mode, s.discount_kind, s.discount_value, s.shipping_amount, s.shipping_tax_code_id,
               s.subtotal_amount, s.discount_amount, s.tax_amount, s.total_amount,
               s.memo_to_customer, s.internal_note,
               s.source_quote_id, q.quote_number AS source_quote_number,
               s.invoice_number,
               s.customer_po_number,
               s.confirmed_at,
               s.created_at, s.updated_at
          FROM sales_orders s
          LEFT JOIN customers c ON c.id = s.customer_id
          LEFT JOIN quotes q ON q.id = s.source_quote_id
        """;

    private static SalesOrderSummary MapSummary(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: reader.GetGuid(1),
        SalesOrderNumber: reader.GetString(2),
        CustomerId: reader.GetGuid(3),
        CustomerName: reader.GetString(4),
        DocumentDate: DateOnly.FromDateTime(reader.GetDateTime(5)),
        Status: reader.GetString(6),
        TransactionCurrencyCode: reader.GetString(7),
        TotalAmount: reader.GetDecimal(8),
        SourceQuoteId: reader.IsDBNull(9) ? null : reader.GetGuid(9),
        InvoiceNumber: reader.IsDBNull(10) ? null : reader.GetString(10),
        CustomerPoNumber: reader.IsDBNull(11) ? null : reader.GetString(11),
        ConfirmedAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(13),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(14));

    private static SalesOrderRecord MapRecord(NpgsqlDataReader reader, IReadOnlyList<SalesOrderLineRecord> lines) => new(
        Id: reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId: reader.GetGuid(reader.GetOrdinal("company_id")),
        SalesOrderNumber: reader.GetString(reader.GetOrdinal("sales_order_number")),
        Status: reader.GetString(reader.GetOrdinal("status")),
        CustomerId: reader.GetGuid(reader.GetOrdinal("customer_id")),
        CustomerName: reader.GetString(reader.GetOrdinal("customer_name")),
        DocumentDate: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("document_date"))),
        TransactionCurrencyCode: reader.GetString(reader.GetOrdinal("transaction_currency_code")),
        FxRate: ReadNullableDecimal(reader, "fx_rate"),
        BillingAddressLine: ReadNullableString(reader, "billing_address_line"),
        BillingCity: ReadNullableString(reader, "billing_city"),
        BillingProvinceState: ReadNullableString(reader, "billing_province_state"),
        BillingPostalCode: ReadNullableString(reader, "billing_postal_code"),
        BillingCountry: ReadNullableString(reader, "billing_country"),
        ShippingAddressLine: ReadNullableString(reader, "shipping_address_line"),
        ShippingCity: ReadNullableString(reader, "shipping_city"),
        ShippingProvinceState: ReadNullableString(reader, "shipping_province_state"),
        ShippingPostalCode: ReadNullableString(reader, "shipping_postal_code"),
        ShippingCountry: ReadNullableString(reader, "shipping_country"),
        ShipVia: ReadNullableString(reader, "ship_via"),
        ShippingDate: ReadNullableDate(reader, "shipping_date"),
        TrackingNo: ReadNullableString(reader, "tracking_no"),
        TaxMode: reader.GetString(reader.GetOrdinal("tax_mode")),
        DiscountKind: ReadNullableString(reader, "discount_kind"),
        DiscountValue: ReadNullableDecimal(reader, "discount_value"),
        ShippingAmount: ReadNullableDecimal(reader, "shipping_amount"),
        ShippingTaxCodeId: ReadNullableGuid(reader, "shipping_tax_code_id"),
        SubtotalAmount: reader.GetDecimal(reader.GetOrdinal("subtotal_amount")),
        DiscountAmount: reader.GetDecimal(reader.GetOrdinal("discount_amount")),
        TaxAmount: reader.GetDecimal(reader.GetOrdinal("tax_amount")),
        TotalAmount: reader.GetDecimal(reader.GetOrdinal("total_amount")),
        MemoToCustomer: ReadNullableString(reader, "memo_to_customer"),
        InternalNote: ReadNullableString(reader, "internal_note"),
        SourceQuoteId: ReadNullableGuid(reader, "source_quote_id"),
        SourceQuoteNumber: ReadNullableString(reader, "source_quote_number"),
        InvoiceNumber: ReadNullableString(reader, "invoice_number"),
        CustomerPoNumber: ReadNullableString(reader, "customer_po_number"),
        ConfirmedAt: ReadNullableTimestamp(reader, "confirmed_at"),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
        Lines: lines);

    private static string? ReadNullableString(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private static Guid? ReadNullableGuid(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static DateOnly? ReadNullableDate(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    }

    private static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }
}
