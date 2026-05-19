using Citus.Accounting.Application.Repositories;
using Modules.AP;
using Modules.AP.Bills;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.AP.Bills;

/// <summary>
/// PostgreSQL backing for <see cref="IBillStore"/>. The base
/// <c>bills</c> + <c>bill_lines</c> tables come from the migration
/// draft (line 811); this store layers on the three columns the
/// Bill page needs (<c>payment_term_id</c>,
/// <c>source_purchase_order_id</c>, <c>source_purchase_order_number</c>)
/// by verifying the migration-installed schema instead of applying DDL
/// from the application process.
///
/// V1 keeps Post / Void as pure status transitions — the heavy
/// posting integration (FX snapshot, AP open item, journal-entry
/// writes via <c>PostBillCommandHandler</c>) is a separate batch.
/// </summary>
public sealed class PostgreSqlBillStore(PostgreSqlConnectionFactory connections) : IBillStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await PostgreSqlSchemaChecks.EnsureTableColumnsAsync(
            connections,
            "bills",
            new[]
            {
                "id",
                "company_id",
                "entity_number",
                "bill_number",
                "vendor_id",
                "status",
                "bill_date",
                "due_date",
                "document_currency_code",
                "base_currency_code",
                "fx_rate",
                "subtotal_amount",
                "tax_amount",
                "total_amount",
                "payment_term_id",
                "source_purchase_order_id",
                "source_purchase_order_number",
                "created_by_user_id",
                "created_at",
                "updated_at"
            },
            "Bill schema has not been installed. Apply database migrations before using AP bills.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BillSummary>> ListAsync(
        CompanyId companyId,
        BillListFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = new List<BillSummary>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sql = """
            SELECT b.id, b.company_id, b.entity_number, b.bill_number,
                   b.vendor_id, COALESCE(v.display_name, '') AS vendor_name,
                   b.bill_date, b.due_date, b.status,
                   b.document_currency_code, b.total_amount,
                   b.source_purchase_order_number,
                   b.created_at, b.updated_at
              FROM bills b
              LEFT JOIN vendors v ON v.id = b.vendor_id
             WHERE b.company_id = @company_id
            """;
        if (!filter.IncludeDrafts)
        {
            sql += " AND b.status <> 'draft'";
        }
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            sql += " AND b.status = @status";
            command.Parameters.AddWithValue("status", filter.Status);
        }
        if (filter.VendorId is { } vendorId)
        {
            sql += " AND b.vendor_id = @vendor_id";
            command.Parameters.AddWithValue("vendor_id", vendorId);
        }
        if (filter.FromDate is { } fromDate)
        {
            sql += " AND b.bill_date >= @from_date";
            command.Parameters.Add("from_date", NpgsqlDbType.Date).Value = fromDate.ToDateTime(TimeOnly.MinValue);
        }
        if (filter.ToDate is { } toDate)
        {
            sql += " AND b.bill_date <= @to_date";
            command.Parameters.Add("to_date", NpgsqlDbType.Date).Value = toDate.ToDateTime(TimeOnly.MinValue);
        }
        sql += " ORDER BY b.bill_date DESC, b.created_at DESC;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapSummary(reader));
        }
        return rows;
    }

    public async Task<BillRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        BillRecord? bill;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectBillColumns + " WHERE b.company_id = @company_id AND b.id = @id LIMIT 1;";
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("id", billId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            bill = MapRecord(reader, lines: Array.Empty<BillLineRecord>());
        }

        var lines = await ReadLinesAsync(connection, billId, cancellationToken).ConfigureAwait(false);
        return bill with { Lines = lines };
    }

    public async Task<BillRecord> CreateAsync(
        CompanyId companyId,
        UserId createdByUserId,
        BillUpsertInput input,
        CancellationToken cancellationToken)
    {
        var entityNumber = GenerateEntityNumber();

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        var baseCurrencyCode = await ReadBaseCurrencyAsync(connection, companyId, transaction: null, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid billId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO bills (
                    company_id, entity_number, bill_number, vendor_id, status,
                    bill_date, due_date,
                    document_currency_code, base_currency_code,
                    fx_rate, fx_requested_date, fx_effective_date, fx_source,
                    subtotal_amount, tax_amount, total_amount,
                    memo,
                    payment_term_id, source_purchase_order_id, source_purchase_order_number,
                    created_by_user_id
                )
                VALUES (
                    @company_id, @entity_number, @bill_number, @vendor_id, 'draft',
                    @bill_date, @due_date,
                    @document_currency_code, @base_currency_code,
                    @fx_rate, @fx_requested_date, @fx_effective_date, @fx_source,
                    @subtotal_amount, @tax_amount, @total_amount,
                    @memo,
                    @payment_term_id, @source_purchase_order_id, @source_purchase_order_number,
                    @created_by_user_id
                )
                RETURNING id;
                """;
            BindUpsertParameters(command, companyId, input, baseCurrencyCode);
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);
            billId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await InsertLinesAsync(connection, transaction, companyId, billId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var saved = await GetByIdAsync(companyId, billId, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException("Bill insert returned no row.");
    }

    public async Task<BillRecord?> UpdateAsync(
        CompanyId companyId,
        Guid billId,
        BillUpsertInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        var baseCurrencyCode = await ReadBaseCurrencyAsync(connection, companyId, transaction: null, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var statusCmd = connection.CreateCommand())
        {
            statusCmd.Transaction = transaction;
            statusCmd.CommandText = "SELECT status FROM bills WHERE company_id = @company_id AND id = @id LIMIT 1;";
            statusCmd.Parameters.AddWithValue("company_id", companyId.Value);
            statusCmd.Parameters.AddWithValue("id", billId);
            var statusObj = await statusCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (statusObj is null) return null;
            var currentStatus = (string)statusObj;
            if (!BillStatus.IsEditable(currentStatus))
            {
                throw new InvalidOperationException(
                    $"Bill in status '{currentStatus}' cannot be edited. Only Draft bills are editable.");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Optimistic-concurrency guard. The follow-up UPDATE narrows
            // on updated_at when the caller passed a non-null
            // ExpectedUpdatedAt. Zero rows affected after passing the
            // status guard above ⇒ the timestamp drifted ⇒ raise
            // ConcurrencyConflictException so the route returns 409
            // and the operator can refresh-and-retry.
            command.CommandText = """
                UPDATE bills
                   SET bill_number              = @bill_number,
                       vendor_id                = @vendor_id,
                       bill_date                = @bill_date,
                       due_date                 = @due_date,
                       document_currency_code   = @document_currency_code,
                       base_currency_code       = @base_currency_code,
                       fx_rate                  = @fx_rate,
                       fx_requested_date        = @fx_requested_date,
                       fx_effective_date        = @fx_effective_date,
                       fx_source                = @fx_source,
                       subtotal_amount          = @subtotal_amount,
                       tax_amount               = @tax_amount,
                       total_amount             = @total_amount,
                       memo                     = @memo,
                       payment_term_id          = @payment_term_id,
                       source_purchase_order_id = @source_purchase_order_id,
                       source_purchase_order_number = @source_purchase_order_number,
                       updated_at               = NOW()
                 WHERE company_id = @company_id AND id = @id
                   AND (cast(@expected_updated_at as timestamptz) IS NULL
                        OR updated_at = cast(@expected_updated_at as timestamptz));
                """;
            BindUpsertParameters(command, companyId, input, baseCurrencyCode);
            command.Parameters.AddWithValue("id", billId);
            command.Parameters.AddWithValue(
                "expected_updated_at",
                input.ExpectedUpdatedAt.HasValue
                    ? (object)input.ExpectedUpdatedAt.Value
                    : DBNull.Value);
            var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affectedRows != 1 && input.ExpectedUpdatedAt.HasValue)
            {
                throw new ConcurrencyConflictException(
                    "This bill was modified by another session after you opened it. " +
                    "Reload the bill to see the latest changes, then re-apply your edits.");
            }
        }

        await using (var deleteLines = connection.CreateCommand())
        {
            deleteLines.Transaction = transaction;
            deleteLines.CommandText = "DELETE FROM bill_lines WHERE bill_id = @id;";
            deleteLines.Parameters.AddWithValue("id", billId);
            await deleteLines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertLinesAsync(connection, transaction, companyId, billId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, billId, cancellationToken).ConfigureAwait(false);
    }

    public Task<BillRecord?> PostAsync(
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken) =>
        Task.FromException<BillRecord?>(
            new InvalidOperationException(
                "Bill posting is disabled on this legacy Bills page because it would not create the required journal entry and AP open item. Use the canonical bill posting workflow."));

    public async Task<BillRecord?> VoidAsync(
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        // Draft cancellation only. Posted bills require a governed
        // reversal/void workflow so the ledger and AP open item stay
        // traceable.
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bills
               SET status     = 'voided',
                   updated_at = NOW()
             WHERE company_id = @company_id AND id = @id
               AND status = 'draft'
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", billId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT status FROM bills WHERE company_id = @company_id AND id = @id LIMIT 1;";
            checkCmd.Parameters.AddWithValue("company_id", companyId.Value);
            checkCmd.Parameters.AddWithValue("id", billId);
            var status = (string?)await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (status is null) return null;
            throw new InvalidOperationException(
                status.Equals("posted", StringComparison.OrdinalIgnoreCase)
                    ? "Posted bills cannot be voided from the legacy Bills page because that would not reverse the journal entry or AP open item. Use the governed bill reversal workflow."
                    : $"Bill in status '{status}' cannot be voided.");
        }
        return await GetByIdAsync(companyId, billId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<BillLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        Guid billId,
        CancellationToken cancellationToken)
    {
        var lines = new List<BillLineRecord>();
        await using var command = connection.CreateCommand();
        // task_id column added by PostgresTaskLinkSchemaInitializer
        // (Batch 8). Reading it here lets the edit page pre-fill the
        // per-line TaskPicker.
        command.CommandText = """
            SELECT id, bill_id, line_number, expense_account_id, description,
                   line_amount, tax_code_id, tax_amount, task_id
              FROM bill_lines
             WHERE bill_id = @bill_id
             ORDER BY line_number;
            """;
        command.Parameters.AddWithValue("bill_id", billId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new BillLineRecord(
                Id: reader.GetGuid(0),
                BillId: reader.GetGuid(1),
                LineNumber: reader.GetInt32(2),
                ExpenseAccountId: reader.GetGuid(3),
                Description: reader.GetString(4),
                LineAmount: reader.GetDecimal(5),
                TaxCodeId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
                TaxAmount: reader.GetDecimal(7),
                TaskId: reader.IsDBNull(8) ? null : reader.GetGuid(8)));
        }
        return lines;
    }

    private static async Task InsertLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billId,
        IReadOnlyList<BillLineInput> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO bill_lines (
                    company_id, bill_id, line_number, expense_account_id, description,
                    line_amount, tax_code_id, tax_amount, is_tax_recoverable, task_id)
                VALUES (
                    @company_id, @bill_id, @line_number, @expense_account_id, @description,
                    @line_amount, @tax_code_id, @tax_amount, FALSE, @task_id);
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("bill_id", billId);
            command.Parameters.AddWithValue("line_number", line.LineNumber);
            command.Parameters.AddWithValue("expense_account_id", line.ExpenseAccountId);
            command.Parameters.AddWithValue("description", line.Description ?? string.Empty);
            command.Parameters.AddWithValue("line_amount", line.LineAmount);
            command.Parameters.AddWithValue("tax_code_id", (object?)line.TaxCodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("tax_amount", line.TaxAmount);
            // task_id column added by PostgresTaskLinkSchemaInitializer
            // (Batch 8). Validator already rejected billed / canceled /
            // cross-company task ids at the route layer.
            command.Parameters.AddWithValue("task_id", (object?)line.TaskId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void BindUpsertParameters(
        NpgsqlCommand command,
        CompanyId companyId,
        BillUpsertInput input,
        string baseCurrencyCode)
    {
        var documentCurrency = input.DocumentCurrencyCode.Trim().ToUpperInvariant();
        var sameCurrency = string.Equals(documentCurrency, baseCurrencyCode, StringComparison.Ordinal);
        var fxRate = FxRatePostingPolicy.ResolveTransactionToBaseRate(
            input.FxRate,
            documentCurrency,
            baseCurrencyCode,
            "bill");
        var fxSource = sameCurrency ? "identity" : "manual";

        var (subtotal, tax, total) = ComputeTotals(input);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_number", input.BillNumber.Trim());
        command.Parameters.AddWithValue("vendor_id", input.VendorId);
        command.Parameters.Add("bill_date", NpgsqlDbType.Date).Value = input.BillDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("due_date", NpgsqlDbType.Date).Value = input.DueDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.AddWithValue("document_currency_code", documentCurrency);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("fx_rate", fxRate);
        command.Parameters.Add("fx_requested_date", NpgsqlDbType.Date).Value = input.BillDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("fx_effective_date", NpgsqlDbType.Date).Value = input.BillDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.AddWithValue("fx_source", fxSource);
        command.Parameters.AddWithValue("subtotal_amount", subtotal);
        command.Parameters.AddWithValue("tax_amount", tax);
        command.Parameters.AddWithValue("total_amount", total);
        command.Parameters.AddWithValue("memo", (object?)input.Memo ?? DBNull.Value);
        command.Parameters.AddWithValue("payment_term_id", (object?)input.PaymentTermId ?? DBNull.Value);
        command.Parameters.AddWithValue("source_purchase_order_id", (object?)input.SourcePurchaseOrderId ?? DBNull.Value);
        command.Parameters.AddWithValue("source_purchase_order_number", (object?)input.SourcePurchaseOrderNumber ?? DBNull.Value);
    }

    private static async Task<string> ReadBaseCurrencyAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = "SELECT base_currency_code FROM companies WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("id", companyId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
        {
            throw new InvalidOperationException(
                $"Company {companyId:D} has no base_currency_code. Run the company provisioning workflow first.");
        }
        return ((string)result).Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Server-side totals from line input. V1 does not yet integrate
    /// with the tax engine; <c>tax_amount</c> sums the per-line tax
    /// figures the caller supplies (zero is fine). Subtotal is the
    /// sum of line_amount; total = subtotal + tax.
    /// </summary>
    private static (decimal subtotal, decimal tax, decimal total) ComputeTotals(BillUpsertInput input)
    {
        decimal subtotal = 0m;
        decimal tax = 0m;
        foreach (var line in input.Lines)
        {
            subtotal += Math.Round(line.LineAmount, 6);
            tax += Math.Round(line.TaxAmount, 6);
        }
        return (subtotal, tax, Math.Round(subtotal + tax, 6));
    }

    /// <summary>
    /// EN{4-digit-year}{8-digit-random}. Matches the
    /// <c>bills_entity_number_format_chk</c> constraint. Collisions on
    /// the unique index are handled by the API layer's retry hint.
    /// </summary>
    private static string GenerateEntityNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, (int)EntityNumber.MaxOrdinal + 1);
        return EntityNumber.Create(year, seed).Value;
    }

    private const string SelectBillColumns = """
        SELECT b.id, b.company_id, b.entity_number, b.bill_number, b.status,
               b.vendor_id, COALESCE(v.display_name, '') AS vendor_name,
               b.bill_date, b.due_date,
               b.document_currency_code, b.base_currency_code,
               b.fx_rate, b.fx_source,
               b.subtotal_amount, b.tax_amount, b.total_amount,
               b.memo,
               b.payment_term_id, b.source_purchase_order_id, b.source_purchase_order_number,
               b.posted_at, b.created_at, b.updated_at
          FROM bills b
          LEFT JOIN vendors v ON v.id = b.vendor_id
        """;

    private static BillSummary MapSummary(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: CompanyId.Parse(reader.GetString(1)),
        EntityNumber: reader.GetString(2),
        BillNumber: reader.GetString(3),
        VendorId: reader.GetGuid(4),
        VendorName: reader.GetString(5),
        BillDate: DateOnly.FromDateTime(reader.GetDateTime(6)),
        DueDate: DateOnly.FromDateTime(reader.GetDateTime(7)),
        Status: reader.GetString(8),
        DocumentCurrencyCode: reader.GetString(9),
        TotalAmount: reader.GetDecimal(10),
        SourcePurchaseOrderNumber: reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(12),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(13));

    private static BillRecord MapRecord(NpgsqlDataReader reader, IReadOnlyList<BillLineRecord> lines) => new(
        Id: reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
        EntityNumber: reader.GetString(reader.GetOrdinal("entity_number")),
        BillNumber: reader.GetString(reader.GetOrdinal("bill_number")),
        Status: reader.GetString(reader.GetOrdinal("status")),
        VendorId: reader.GetGuid(reader.GetOrdinal("vendor_id")),
        VendorName: reader.GetString(reader.GetOrdinal("vendor_name")),
        BillDate: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("bill_date"))),
        DueDate: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("due_date"))),
        DocumentCurrencyCode: reader.GetString(reader.GetOrdinal("document_currency_code")),
        BaseCurrencyCode: reader.GetString(reader.GetOrdinal("base_currency_code")),
        FxRate: reader.GetDecimal(reader.GetOrdinal("fx_rate")),
        FxSource: reader.GetString(reader.GetOrdinal("fx_source")),
        SubtotalAmount: reader.GetDecimal(reader.GetOrdinal("subtotal_amount")),
        TaxAmount: reader.GetDecimal(reader.GetOrdinal("tax_amount")),
        TotalAmount: reader.GetDecimal(reader.GetOrdinal("total_amount")),
        Memo: ReadNullableString(reader, "memo"),
        PaymentTermId: ReadNullableGuid(reader, "payment_term_id"),
        SourcePurchaseOrderId: ReadNullableGuid(reader, "source_purchase_order_id"),
        SourcePurchaseOrderNumber: ReadNullableString(reader, "source_purchase_order_number"),
        PostedAt: ReadNullableDateTimeOffset(reader, "posted_at"),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
        Lines: lines);

    private static string? ReadNullableString(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? ReadNullableGuid(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }
}
