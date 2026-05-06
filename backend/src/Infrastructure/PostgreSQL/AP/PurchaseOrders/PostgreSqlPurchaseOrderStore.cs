using Modules.AP.PurchaseOrders;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.AP.PurchaseOrders;

/// <summary>
/// PostgreSQL backing for <see cref="IPurchaseOrderStore"/>. Owns the
/// <c>ap_purchase_orders</c> + <c>ap_purchase_order_lines</c> tables —
/// distinct from the inventory-grade <c>purchase_orders</c> tables
/// owned by <see cref="Citus.Accounting.Infrastructure.Persistence.PostgresPurchaseOrderDocumentRepository"/>.
/// PO numbering follows <c>PO{year}{8-digit-random}</c> per the
/// Tralanz numbering convention.
/// </summary>
public sealed class PostgreSqlPurchaseOrderStore(PostgreSqlConnectionFactory connections) : IPurchaseOrderStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ap_purchase_orders (
                id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id                  UUID NOT NULL,
                purchase_order_number       TEXT NOT NULL,
                status                      TEXT NOT NULL DEFAULT 'draft',
                vendor_id                   UUID NOT NULL,
                order_date                  DATE NOT NULL,
                expected_delivery_date      DATE NULL,
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
                memo_to_supplier            TEXT NULL,
                internal_note               TEXT NULL,
                payment_term_id             UUID NULL,
                created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uq_ap_purchase_orders_company_po_number
                ON ap_purchase_orders (company_id, purchase_order_number);
            CREATE INDEX IF NOT EXISTS idx_ap_purchase_orders_company_status
                ON ap_purchase_orders (company_id, status);
            CREATE INDEX IF NOT EXISTS idx_ap_purchase_orders_company_vendor
                ON ap_purchase_orders (company_id, vendor_id);
            CREATE INDEX IF NOT EXISTS idx_ap_purchase_orders_company_order_date
                ON ap_purchase_orders (company_id, order_date DESC);

            CREATE TABLE IF NOT EXISTS ap_purchase_order_lines (
                id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                purchase_order_id   UUID NOT NULL REFERENCES ap_purchase_orders(id) ON DELETE CASCADE,
                sequence            INTEGER NOT NULL,
                service_date        DATE NULL,
                item_id             UUID NULL,
                expense_account_id  UUID NULL,
                description         TEXT NOT NULL DEFAULT '',
                quantity            NUMERIC(18,4) NOT NULL DEFAULT 0,
                unit_price          NUMERIC(18,4) NOT NULL DEFAULT 0,
                tax_code_id         UUID NULL,
                line_total          NUMERIC(18,4) NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_ap_purchase_order_lines_po
                ON ap_purchase_order_lines (purchase_order_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseOrderSummary>> ListAsync(
        CompanyId companyId,
        PurchaseOrderListFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = new List<PurchaseOrderSummary>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sql = """
            SELECT p.id, p.company_id, p.purchase_order_number, p.vendor_id,
                   COALESCE(v.display_name, '') AS vendor_name,
                   p.order_date, p.expected_delivery_date, p.status,
                   p.transaction_currency_code, p.total_amount,
                   p.created_at, p.updated_at
              FROM ap_purchase_orders p
              LEFT JOIN vendors v ON v.id = p.vendor_id
             WHERE p.company_id = @company_id
            """;
        if (!filter.IncludeDrafts)
        {
            sql += " AND p.status <> 'draft'";
        }
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            sql += " AND p.status = @status";
            command.Parameters.AddWithValue("status", filter.Status);
        }
        if (filter.VendorId is { } vendorId)
        {
            sql += " AND p.vendor_id = @vendor_id";
            command.Parameters.AddWithValue("vendor_id", vendorId);
        }
        if (filter.FromDate is { } fromDate)
        {
            sql += " AND p.order_date >= @from_date";
            command.Parameters.Add("from_date", NpgsqlDbType.Date).Value = fromDate.ToDateTime(TimeOnly.MinValue);
        }
        if (filter.ToDate is { } toDate)
        {
            sql += " AND p.order_date <= @to_date";
            command.Parameters.Add("to_date", NpgsqlDbType.Date).Value = toDate.ToDateTime(TimeOnly.MinValue);
        }
        sql += " ORDER BY p.order_date DESC, p.created_at DESC;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapSummary(reader));
        }
        return rows;
    }

    public async Task<PurchaseOrderRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        PurchaseOrderRecord? po;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectColumns + " WHERE p.company_id = @company_id AND p.id = @id LIMIT 1;";
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("id", purchaseOrderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            po = MapRecord(reader, lines: Array.Empty<PurchaseOrderLineRecord>());
        }

        var lines = await ReadLinesAsync(connection, purchaseOrderId, cancellationToken).ConfigureAwait(false);
        return po with { Lines = lines };
    }

    public async Task<PurchaseOrderRecord> CreateAsync(
        CompanyId companyId,
        PurchaseOrderUpsertInput input,
        CancellationToken cancellationToken)
    {
        var (subtotal, discount, tax, total) = ComputeTotals(input);
        var poNumber = GeneratePurchaseOrderNumber();

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid poId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ap_purchase_orders (
                    company_id, purchase_order_number, status, vendor_id,
                    order_date, expected_delivery_date,
                    transaction_currency_code, fx_rate,
                    billing_address_line, billing_city, billing_province_state, billing_postal_code, billing_country,
                    shipping_address_line, shipping_city, shipping_province_state, shipping_postal_code, shipping_country,
                    ship_via, shipping_date, tracking_no,
                    tax_mode, discount_kind, discount_value, shipping_amount, shipping_tax_code_id,
                    subtotal_amount, discount_amount, tax_amount, total_amount,
                    memo_to_supplier, internal_note, payment_term_id
                )
                VALUES (
                    @company_id, @purchase_order_number, 'draft', @vendor_id,
                    @order_date, @expected_delivery_date,
                    @transaction_currency_code, @fx_rate,
                    @billing_address_line, @billing_city, @billing_province_state, @billing_postal_code, @billing_country,
                    @shipping_address_line, @shipping_city, @shipping_province_state, @shipping_postal_code, @shipping_country,
                    @ship_via, @shipping_date, @tracking_no,
                    @tax_mode, @discount_kind, @discount_value, @shipping_amount, @shipping_tax_code_id,
                    @subtotal_amount, @discount_amount, @tax_amount, @total_amount,
                    @memo_to_supplier, @internal_note, @payment_term_id
                )
                RETURNING id;
                """;
            BindUpsertParameters(command, companyId, input, subtotal, discount, tax, total);
            command.Parameters.AddWithValue("purchase_order_number", poNumber);
            poId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await InsertLinesAsync(connection, transaction, poId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var saved = await GetByIdAsync(companyId, poId, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException("Purchase order insert returned no row.");
    }

    public async Task<PurchaseOrderRecord?> UpdateAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        PurchaseOrderUpsertInput input,
        CancellationToken cancellationToken)
    {
        var (subtotal, discount, tax, total) = ComputeTotals(input);

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var statusCmd = connection.CreateCommand())
        {
            statusCmd.Transaction = transaction;
            statusCmd.CommandText = "SELECT status FROM ap_purchase_orders WHERE company_id = @company_id AND id = @id LIMIT 1;";
            statusCmd.Parameters.AddWithValue("company_id", companyId.Value);
            statusCmd.Parameters.AddWithValue("id", purchaseOrderId);
            var statusObj = await statusCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null) return null;
            var currentStatus = (string)statusObj;
            if (!PurchaseOrderStatus.IsEditable(currentStatus))
            {
                throw new InvalidOperationException(
                    $"Purchase order in status '{currentStatus}' cannot be edited. Only Draft and Open POs are editable.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ap_purchase_orders
                   SET vendor_id                 = @vendor_id,
                       order_date                = @order_date,
                       expected_delivery_date    = @expected_delivery_date,
                       transaction_currency_code = @transaction_currency_code,
                       fx_rate                   = @fx_rate,
                       billing_address_line      = @billing_address_line,
                       billing_city              = @billing_city,
                       billing_province_state    = @billing_province_state,
                       billing_postal_code       = @billing_postal_code,
                       billing_country           = @billing_country,
                       shipping_address_line     = @shipping_address_line,
                       shipping_city             = @shipping_city,
                       shipping_province_state   = @shipping_province_state,
                       shipping_postal_code      = @shipping_postal_code,
                       shipping_country          = @shipping_country,
                       ship_via                  = @ship_via,
                       shipping_date             = @shipping_date,
                       tracking_no               = @tracking_no,
                       tax_mode                  = @tax_mode,
                       discount_kind             = @discount_kind,
                       discount_value            = @discount_value,
                       shipping_amount           = @shipping_amount,
                       shipping_tax_code_id      = @shipping_tax_code_id,
                       subtotal_amount           = @subtotal_amount,
                       discount_amount           = @discount_amount,
                       tax_amount                = @tax_amount,
                       total_amount              = @total_amount,
                       memo_to_supplier          = @memo_to_supplier,
                       internal_note             = @internal_note,
                       payment_term_id           = @payment_term_id,
                       updated_at                = NOW()
                 WHERE company_id = @company_id AND id = @id;
                """;
            BindUpsertParameters(command, companyId, input, subtotal, discount, tax, total);
            command.Parameters.AddWithValue("id", purchaseOrderId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteLines = connection.CreateCommand())
        {
            deleteLines.Transaction = transaction;
            deleteLines.CommandText = "DELETE FROM ap_purchase_order_lines WHERE purchase_order_id = @id;";
            deleteLines.Parameters.AddWithValue("id", purchaseOrderId);
            await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertLinesAsync(connection, transaction, purchaseOrderId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, purchaseOrderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchaseOrderRecord?> SetStatusAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        string newStatus,
        CancellationToken cancellationToken)
    {
        if (!PurchaseOrderStatus.IsValid(newStatus))
        {
            throw new InvalidOperationException($"Invalid status '{newStatus}'.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ap_purchase_orders
               SET status     = @new_status,
                   updated_at = NOW()
             WHERE company_id = @company_id AND id = @id
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", purchaseOrderId);
        command.Parameters.AddWithValue("new_status", newStatus);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return null;

        return await GetByIdAsync(companyId, purchaseOrderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchaseOrderRecord?> MarkClosedAsync(
        CompanyId companyId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ap_purchase_orders
               SET status     = 'closed',
                   updated_at = NOW()
             WHERE company_id = @company_id AND id = @id
               AND status IN ('open', 'draft', 'closed')
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", purchaseOrderId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return null;

        return await GetByIdAsync(companyId, purchaseOrderId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PurchaseOrderLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        Guid purchaseOrderId,
        CancellationToken cancellationToken)
    {
        var lines = new List<PurchaseOrderLineRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, purchase_order_id, sequence, service_date,
                   item_id, expense_account_id, description,
                   quantity, unit_price, tax_code_id, line_total
              FROM ap_purchase_order_lines
             WHERE purchase_order_id = @po_id
             ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("po_id", purchaseOrderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new PurchaseOrderLineRecord(
                Id: reader.GetGuid(0),
                PurchaseOrderId: reader.GetGuid(1),
                Sequence: reader.GetInt32(2),
                ServiceDate: reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                ItemId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                ExpenseAccountId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
                Description: reader.GetString(6),
                Quantity: reader.GetDecimal(7),
                UnitPrice: reader.GetDecimal(8),
                TaxCodeId: reader.IsDBNull(9) ? null : reader.GetGuid(9),
                LineTotal: reader.GetDecimal(10)));
        }
        return lines;
    }

    private static async Task InsertLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid purchaseOrderId,
        IReadOnlyList<PurchaseOrderLineInput> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ap_purchase_order_lines (
                    purchase_order_id, sequence, service_date,
                    item_id, expense_account_id, description,
                    quantity, unit_price, tax_code_id, line_total)
                VALUES (
                    @po_id, @sequence, @service_date,
                    @item_id, @expense_account_id, @description,
                    @quantity, @unit_price, @tax_code_id, @line_total);
                """;
            command.Parameters.AddWithValue("po_id", purchaseOrderId);
            command.Parameters.AddWithValue("sequence", line.Sequence);
            command.Parameters.Add("service_date", NpgsqlDbType.Date).Value =
                line.ServiceDate is { } svc ? svc.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
            command.Parameters.AddWithValue("item_id", (object?)line.ItemId ?? DBNull.Value);
            command.Parameters.AddWithValue("expense_account_id", (object?)line.ExpenseAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("description", line.Description ?? string.Empty);
            command.Parameters.AddWithValue("quantity", line.Quantity);
            command.Parameters.AddWithValue("unit_price", line.UnitPrice);
            command.Parameters.AddWithValue("tax_code_id", (object?)line.TaxCodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("line_total", Math.Round(line.Quantity * line.UnitPrice, 4));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static (decimal subtotal, decimal discount, decimal tax, decimal total) ComputeTotals(PurchaseOrderUpsertInput input)
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

        decimal tax = 0m; // V1: tax engine not wired; line-level tax_code captures intent for posting.
        decimal shipping = Math.Round(input.ShippingAmount ?? 0m, 4);

        decimal total = string.Equals(input.TaxMode, "inclusive", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(subtotal - discount + shipping, 4)
            : Math.Round(subtotal - discount + tax + shipping, 4);

        return (subtotal, discount, tax, total);
    }

    private static void BindUpsertParameters(
        NpgsqlCommand command,
        CompanyId companyId,
        PurchaseOrderUpsertInput input,
        decimal subtotal,
        decimal discount,
        decimal tax,
        decimal total)
    {
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("vendor_id", input.VendorId);
        command.Parameters.Add("order_date", NpgsqlDbType.Date).Value = input.OrderDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("expected_delivery_date", NpgsqlDbType.Date).Value =
            input.ExpectedDeliveryDate is { } exp ? exp.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
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
            input.ShippingDate is { } ship ? ship.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
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
        command.Parameters.AddWithValue("memo_to_supplier", (object?)input.MemoToSupplier ?? DBNull.Value);
        command.Parameters.AddWithValue("internal_note", (object?)input.InternalNote ?? DBNull.Value);
        command.Parameters.AddWithValue("payment_term_id", (object?)input.PaymentTermId ?? DBNull.Value);
    }

    /// <summary>PO{4-digit-year}{8-digit-random} per the Tralanz numbering convention.</summary>
    private static string GeneratePurchaseOrderNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, 100_000_000);
        return $"PO{year:0000}{seed:00000000}";
    }

    private const string SelectColumns = """
        SELECT p.id, p.company_id, p.purchase_order_number, p.status, p.vendor_id,
               COALESCE(v.display_name, '') AS vendor_name,
               p.order_date, p.expected_delivery_date,
               p.transaction_currency_code, p.fx_rate,
               p.billing_address_line, p.billing_city, p.billing_province_state, p.billing_postal_code, p.billing_country,
               p.shipping_address_line, p.shipping_city, p.shipping_province_state, p.shipping_postal_code, p.shipping_country,
               p.ship_via, p.shipping_date, p.tracking_no,
               p.tax_mode, p.discount_kind, p.discount_value, p.shipping_amount, p.shipping_tax_code_id,
               p.subtotal_amount, p.discount_amount, p.tax_amount, p.total_amount,
               p.memo_to_supplier, p.internal_note, p.payment_term_id,
               p.created_at, p.updated_at
          FROM ap_purchase_orders p
          LEFT JOIN vendors v ON v.id = p.vendor_id
        """;

    private static PurchaseOrderSummary MapSummary(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: reader.GetGuid(1),
        PurchaseOrderNumber: reader.GetString(2),
        VendorId: reader.GetGuid(3),
        VendorName: reader.GetString(4),
        OrderDate: DateOnly.FromDateTime(reader.GetDateTime(5)),
        ExpectedDeliveryDate: reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)),
        Status: reader.GetString(7),
        TransactionCurrencyCode: reader.GetString(8),
        TotalAmount: reader.GetDecimal(9),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(10),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(11));

    private static PurchaseOrderRecord MapRecord(NpgsqlDataReader reader, IReadOnlyList<PurchaseOrderLineRecord> lines) => new(
        Id: reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
        PurchaseOrderNumber: reader.GetString(reader.GetOrdinal("purchase_order_number")),
        Status: reader.GetString(reader.GetOrdinal("status")),
        VendorId: reader.GetGuid(reader.GetOrdinal("vendor_id")),
        VendorName: reader.GetString(reader.GetOrdinal("vendor_name")),
        OrderDate: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("order_date"))),
        ExpectedDeliveryDate: ReadNullableDate(reader, "expected_delivery_date"),
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
        MemoToSupplier: ReadNullableString(reader, "memo_to_supplier"),
        InternalNote: ReadNullableString(reader, "internal_note"),
        PaymentTermId: ReadNullableGuid(reader, "payment_term_id"),
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
}
