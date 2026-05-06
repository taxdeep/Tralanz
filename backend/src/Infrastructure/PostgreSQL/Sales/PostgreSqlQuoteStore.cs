using Citus.Accounting.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.Sales;

/// <summary>
/// PostgreSQL backing for <see cref="IQuoteStore"/>. Owns the
/// <c>quotes</c> + <c>quote_lines</c> tables. Schema is created
/// idempotently from <c>EnsureSchemaAsync</c>; uniqueness is on
/// (company_id, quote_number). Subtotal / discount / tax / total are
/// computed server-side from the line input — never trust the client's
/// number.
/// </summary>
public sealed class PostgreSqlQuoteStore(PostgreSqlConnectionFactory connections) : IQuoteStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS quotes (
                id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id                  UUID NOT NULL,
                quote_number                TEXT NOT NULL,
                status                      TEXT NOT NULL DEFAULT 'draft',
                customer_id                 UUID NOT NULL,
                document_date               DATE NOT NULL,
                expiration_date             DATE NULL,
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
                converted_sales_order_id    UUID NULL,
                customer_po_number          TEXT NULL,
                created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            ALTER TABLE quotes ADD COLUMN IF NOT EXISTS customer_po_number TEXT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS uq_quotes_company_quote_number
                ON quotes (company_id, quote_number);
            CREATE INDEX IF NOT EXISTS idx_quotes_company_status
                ON quotes (company_id, status);
            CREATE INDEX IF NOT EXISTS idx_quotes_company_customer
                ON quotes (company_id, customer_id);
            CREATE INDEX IF NOT EXISTS idx_quotes_company_document_date
                ON quotes (company_id, document_date DESC);
            CREATE INDEX IF NOT EXISTS idx_quotes_company_customer_po
                ON quotes (company_id, customer_po_number)
                WHERE customer_po_number IS NOT NULL;

            CREATE TABLE IF NOT EXISTS quote_lines (
                id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                quote_id        UUID NOT NULL REFERENCES quotes(id) ON DELETE CASCADE,
                sequence        INTEGER NOT NULL,
                service_date    DATE NULL,
                item_id         UUID NULL,
                description     TEXT NOT NULL DEFAULT '',
                quantity        NUMERIC(18,4) NOT NULL DEFAULT 0,
                unit_price      NUMERIC(18,4) NOT NULL DEFAULT 0,
                tax_code_id     UUID NULL,
                account_code    TEXT NULL,
                line_total      NUMERIC(18,4) NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_quote_lines_quote
                ON quote_lines (quote_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuoteSummary>> ListAsync(
        CompanyId companyId,
        QuoteListFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = new List<QuoteSummary>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sql = """
            SELECT q.id, q.company_id, q.quote_number, q.customer_id,
                   COALESCE(c.display_name, '') AS customer_name,
                   q.document_date, q.expiration_date, q.status,
                   q.transaction_currency_code, q.total_amount,
                   q.customer_po_number,
                   q.created_at, q.updated_at
              FROM quotes q
              LEFT JOIN customers c ON c.id = q.customer_id
             WHERE q.company_id = @company_id
            """;
        if (!filter.IncludeDrafts)
        {
            sql += " AND q.status <> 'draft'";
        }
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            sql += " AND q.status = @status";
            command.Parameters.AddWithValue("status", filter.Status);
        }
        if (filter.CustomerId is { } customerId)
        {
            sql += " AND q.customer_id = @customer_id";
            command.Parameters.AddWithValue("customer_id", customerId);
        }
        if (filter.FromDate is { } fromDate)
        {
            sql += " AND q.document_date >= @from_date";
            command.Parameters.AddWithValue("from_date", fromDate);
        }
        if (filter.ToDate is { } toDate)
        {
            sql += " AND q.document_date <= @to_date";
            command.Parameters.AddWithValue("to_date", toDate);
        }
        sql += " ORDER BY q.document_date DESC, q.created_at DESC;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapSummary(reader));
        }
        return rows;
    }

    public async Task<QuoteRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        QuoteRecord? quote;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectQuoteColumns + " WHERE q.company_id = @company_id AND q.id = @id LIMIT 1;";
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("id", quoteId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            quote = MapRecord(reader, lines: Array.Empty<QuoteLineRecord>());
        }

        var lines = await ReadLinesAsync(connection, quoteId, cancellationToken).ConfigureAwait(false);
        return quote with { Lines = lines };
    }

    public async Task<QuoteRecord> CreateAsync(
        CompanyId companyId,
        QuoteUpsertInput input,
        CancellationToken cancellationToken)
    {
        var (subtotal, discount, tax, total) = ComputeTotals(input);
        var quoteNumber = GenerateQuoteNumber();

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid quoteId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quotes (
                    company_id, quote_number, status, customer_id,
                    document_date, expiration_date,
                    transaction_currency_code, fx_rate,
                    billing_address_line, billing_city, billing_province_state, billing_postal_code, billing_country,
                    shipping_address_line, shipping_city, shipping_province_state, shipping_postal_code, shipping_country,
                    ship_via, shipping_date, tracking_no,
                    tax_mode, discount_kind, discount_value, shipping_amount, shipping_tax_code_id,
                    subtotal_amount, discount_amount, tax_amount, total_amount,
                    memo_to_customer, internal_note, customer_po_number
                )
                VALUES (
                    @company_id, @quote_number, 'draft', @customer_id,
                    @document_date, @expiration_date,
                    @transaction_currency_code, @fx_rate,
                    @billing_address_line, @billing_city, @billing_province_state, @billing_postal_code, @billing_country,
                    @shipping_address_line, @shipping_city, @shipping_province_state, @shipping_postal_code, @shipping_country,
                    @ship_via, @shipping_date, @tracking_no,
                    @tax_mode, @discount_kind, @discount_value, @shipping_amount, @shipping_tax_code_id,
                    @subtotal_amount, @discount_amount, @tax_amount, @total_amount,
                    @memo_to_customer, @internal_note, @customer_po_number
                )
                RETURNING id;
                """;
            BindUpsertParameters(command, companyId, input, subtotal, discount, tax, total);
            command.Parameters.AddWithValue("quote_number", quoteNumber);
            quoteId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await InsertLinesAsync(connection, transaction, quoteId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var saved = await GetByIdAsync(companyId, quoteId, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException("Quote insert returned no row.");
    }

    public async Task<QuoteRecord?> UpdateAsync(
        CompanyId companyId,
        Guid quoteId,
        QuoteUpsertInput input,
        CancellationToken cancellationToken)
    {
        var (subtotal, discount, tax, total) = ComputeTotals(input);

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var statusCommand = connection.CreateCommand())
        {
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = "SELECT status FROM quotes WHERE company_id = @company_id AND id = @id LIMIT 1;";
            statusCommand.Parameters.AddWithValue("company_id", companyId.Value);
            statusCommand.Parameters.AddWithValue("id", quoteId);
            var statusObj = await statusCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null)
            {
                return null;
            }
            var currentStatus = (string)statusObj;
            if (!QuoteStatus.IsEditable(currentStatus))
            {
                throw new InvalidOperationException(
                    $"Quote in status '{currentStatus}' cannot be edited. Only Draft and Pending quotes are editable.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE quotes
                   SET customer_id              = @customer_id,
                       document_date            = @document_date,
                       expiration_date          = @expiration_date,
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
            command.Parameters.AddWithValue("id", quoteId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteLines = connection.CreateCommand())
        {
            deleteLines.Transaction = transaction;
            deleteLines.CommandText = "DELETE FROM quote_lines WHERE quote_id = @id;";
            deleteLines.Parameters.AddWithValue("id", quoteId);
            await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertLinesAsync(connection, transaction, quoteId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, quoteId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuoteRecord?> SetStatusAsync(
        CompanyId companyId,
        Guid quoteId,
        string newStatus,
        CancellationToken cancellationToken)
    {
        if (!QuoteStatus.IsValid(newStatus))
        {
            throw new InvalidOperationException($"Invalid status '{newStatus}'.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotes
               SET status = @new_status,
                   updated_at = NOW()
             WHERE company_id = @company_id AND id = @id
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", quoteId);
        command.Parameters.AddWithValue("new_status", newStatus);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return null;

        return await GetByIdAsync(companyId, quoteId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuoteRecord?> MarkConvertedAsync(
        CompanyId companyId,
        Guid quoteId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE quotes
               SET status                   = 'converted',
                   converted_sales_order_id = @sales_order_id,
                   updated_at               = NOW()
             WHERE company_id = @company_id AND id = @id
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", quoteId);
        command.Parameters.AddWithValue("sales_order_id", salesOrderId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return null;

        return await GetByIdAsync(companyId, quoteId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<QuoteLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        var lines = new List<QuoteLineRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, quote_id, sequence, service_date, item_id, description,
                   quantity, unit_price, tax_code_id, account_code, line_total
              FROM quote_lines
             WHERE quote_id = @quote_id
             ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("quote_id", quoteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new QuoteLineRecord(
                Id: reader.GetGuid(0),
                QuoteId: reader.GetGuid(1),
                Sequence: reader.GetInt32(2),
                ServiceDate: reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                ItemId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                Description: reader.GetString(5),
                Quantity: reader.GetDecimal(6),
                UnitPrice: reader.GetDecimal(7),
                TaxCodeId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                AccountCode: reader.IsDBNull(9) ? null : reader.GetString(9),
                LineTotal: reader.GetDecimal(10)));
        }
        return lines;
    }

    private static async Task InsertLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid quoteId,
        IReadOnlyList<QuoteLineInput> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quote_lines (
                    quote_id, sequence, service_date, item_id, description,
                    quantity, unit_price, tax_code_id, account_code, line_total)
                VALUES (
                    @quote_id, @sequence, @service_date, @item_id, @description,
                    @quantity, @unit_price, @tax_code_id, @account_code, @line_total);
                """;
            command.Parameters.AddWithValue("quote_id", quoteId);
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

    /// <summary>
    /// Server-side total computation. Tax math is intentionally simple in V1
    /// — applies a flat tax rate from the line's tax_code if present.
    /// Inclusive vs exclusive only changes whether the line's unit price
    /// already includes tax. Real per-jurisdiction recoverability lands
    /// later via the Posting Engine when conversion to invoice goes live.
    /// </summary>
    private static (decimal subtotal, decimal discount, decimal tax, decimal total) ComputeTotals(QuoteUpsertInput input)
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

        // V1 tax: assume zero tax until the tax engine is wired. The line-level
        // tax_code captures intent for posting; quote-level reporting just
        // shows the subtotal/discount/total.
        decimal tax = 0m;
        decimal shipping = Math.Round(input.ShippingAmount ?? 0m, 4);

        decimal total = string.Equals(input.TaxMode, "inclusive", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(subtotal - discount + shipping, 4)
            : Math.Round(subtotal - discount + tax + shipping, 4);

        return (subtotal, discount, tax, total);
    }

    private static void BindUpsertParameters(
        NpgsqlCommand command,
        CompanyId companyId,
        QuoteUpsertInput input,
        decimal subtotal,
        decimal discount,
        decimal tax,
        decimal total)
    {
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("customer_id", input.CustomerId);
        command.Parameters.Add("document_date", NpgsqlDbType.Date).Value = input.DocumentDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("expiration_date", NpgsqlDbType.Date).Value =
            input.ExpirationDate is { } exp ? exp.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
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
        command.Parameters.AddWithValue("customer_po_number", (object?)input.CustomerPoNumber ?? DBNull.Value);
    }

    /// <summary>
    /// QUO{4-digit-year}{8-digit-random}. Uniqueness enforced by the
    /// per-company composite index; collisions retry from the caller.
    /// </summary>
    private static string GenerateQuoteNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, 100_000_000);
        return $"QUO{year:0000}{seed:00000000}";
    }

    private const string SelectQuoteColumns = """
        SELECT q.id, q.company_id, q.quote_number, q.status, q.customer_id,
               COALESCE(c.display_name, '') AS customer_name,
               q.document_date, q.expiration_date,
               q.transaction_currency_code, q.fx_rate,
               q.billing_address_line, q.billing_city, q.billing_province_state, q.billing_postal_code, q.billing_country,
               q.shipping_address_line, q.shipping_city, q.shipping_province_state, q.shipping_postal_code, q.shipping_country,
               q.ship_via, q.shipping_date, q.tracking_no,
               q.tax_mode, q.discount_kind, q.discount_value, q.shipping_amount, q.shipping_tax_code_id,
               q.subtotal_amount, q.discount_amount, q.tax_amount, q.total_amount,
               q.memo_to_customer, q.internal_note, q.converted_sales_order_id,
               q.customer_po_number,
               q.created_at, q.updated_at
          FROM quotes q
          LEFT JOIN customers c ON c.id = q.customer_id
        """;

    private static QuoteSummary MapSummary(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: reader.GetGuid(1),
        QuoteNumber: reader.GetString(2),
        CustomerId: reader.GetGuid(3),
        CustomerName: reader.GetString(4),
        DocumentDate: DateOnly.FromDateTime(reader.GetDateTime(5)),
        ExpirationDate: reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)),
        Status: reader.GetString(7),
        TransactionCurrencyCode: reader.GetString(8),
        TotalAmount: reader.GetDecimal(9),
        CustomerPoNumber: reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(11),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(12));

    private static QuoteRecord MapRecord(NpgsqlDataReader reader, IReadOnlyList<QuoteLineRecord> lines) => new(
        Id: reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
        QuoteNumber: reader.GetString(reader.GetOrdinal("quote_number")),
        Status: reader.GetString(reader.GetOrdinal("status")),
        CustomerId: reader.GetGuid(reader.GetOrdinal("customer_id")),
        CustomerName: reader.GetString(reader.GetOrdinal("customer_name")),
        DocumentDate: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("document_date"))),
        ExpirationDate: ReadNullableDate(reader, "expiration_date"),
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
        ConvertedSalesOrderId: ReadNullableGuid(reader, "converted_sales_order_id"),
        CustomerPoNumber: ReadNullableString(reader, "customer_po_number"),
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
