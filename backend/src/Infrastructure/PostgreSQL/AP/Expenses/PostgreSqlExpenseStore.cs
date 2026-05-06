using Modules.AP.Expenses;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.AP.Expenses;

/// <summary>
/// PostgreSQL backing for <see cref="IExpenseStore"/>. Owns the
/// <c>expenses</c> + <c>expense_lines</c> tables. EnsureSchemaAsync
/// also runs a one-time idempotent migration that flips legacy
/// "Cash on Hand" rows from <c>detail_type='bank'</c> to
/// <c>detail_type='cash'</c> so the Payment Account picker can group
/// Bank / Cash / Credit Card cleanly.
/// </summary>
public sealed class PostgreSqlExpenseStore(PostgreSqlConnectionFactory connections) : IExpenseStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS expenses (
                id                              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id                      UUID NOT NULL,
                expense_number                  TEXT NOT NULL,
                status                          TEXT NOT NULL DEFAULT 'posted',
                payee_kind                      TEXT NOT NULL,
                payee_id                        UUID NULL,
                payee_name_freeform             TEXT NOT NULL DEFAULT '',
                payment_account_id              UUID NOT NULL,
                payment_method                  TEXT NOT NULL,
                cheque_number                   TEXT NULL,
                ref_no                          TEXT NULL,
                transaction_currency_code       CHAR(3) NOT NULL,
                base_currency_code              CHAR(3) NOT NULL,
                fx_rate                         NUMERIC(18,8) NOT NULL DEFAULT 1,
                fx_source                       TEXT NOT NULL DEFAULT 'identity',
                payment_date                    DATE NOT NULL,
                source_purchase_order_id        UUID NULL,
                source_purchase_order_number    TEXT NULL,
                tax_mode                        TEXT NOT NULL DEFAULT 'exclusive',
                discount_kind                   TEXT NULL,
                discount_value                  NUMERIC(18,4) NULL,
                subtotal_amount                 NUMERIC(18,4) NOT NULL DEFAULT 0,
                discount_amount                 NUMERIC(18,4) NOT NULL DEFAULT 0,
                tax_amount                      NUMERIC(18,4) NOT NULL DEFAULT 0,
                total_amount                    NUMERIC(18,4) NOT NULL DEFAULT 0,
                memo                            TEXT NULL,
                internal_note                   TEXT NULL,
                posted_journal_entry_id         UUID NULL,
                voided_at                       TIMESTAMPTZ NULL,
                created_by_user_id              UUID NOT NULL,
                created_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uq_expenses_company_expense_number
                ON expenses (company_id, expense_number);
            CREATE INDEX IF NOT EXISTS idx_expenses_company_status_date
                ON expenses (company_id, status, payment_date DESC);
            CREATE INDEX IF NOT EXISTS idx_expenses_company_payee
                ON expenses (company_id, payee_id);
            CREATE INDEX IF NOT EXISTS idx_expenses_source_po
                ON expenses (source_purchase_order_id) WHERE source_purchase_order_id IS NOT NULL;

            CREATE TABLE IF NOT EXISTS expense_lines (
                id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                expense_id          UUID NOT NULL REFERENCES expenses(id) ON DELETE CASCADE,
                sequence            INTEGER NOT NULL,
                service_date        DATE NULL,
                item_id             UUID NULL,
                expense_account_id  UUID NOT NULL,
                description         TEXT NOT NULL DEFAULT '',
                quantity            NUMERIC(18,4) NOT NULL DEFAULT 0,
                unit_price          NUMERIC(18,4) NOT NULL DEFAULT 0,
                tax_code_id         UUID NULL,
                line_total          NUMERIC(18,4) NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_expense_lines_expense
                ON expense_lines (expense_id, sequence);

            -- One-time idempotent migration: legacy seed data put "Cash on
            -- Hand" under detail_type='bank'. The Payment Account picker
            -- in Tralanz Books groups by detail_type, so we move it under
            -- 'cash' to match. Matches by name to be safe across already-
            -- provisioned companies; only fires when the row is still on
            -- the old label.
            UPDATE accounts
               SET detail_type = 'cash'
             WHERE name = 'Cash on Hand'
               AND detail_type = 'bank';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExpenseSummary>> ListAsync(
        CompanyId companyId,
        ExpenseListFilter filter,
        CancellationToken cancellationToken)
    {
        var rows = new List<ExpenseSummary>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sql = """
            SELECT e.id, e.company_id, e.expense_number, e.status,
                   e.payee_kind, e.payee_id,
                   COALESCE(NULLIF(e.payee_name_freeform, ''),
                            COALESCE(v.display_name, c.display_name, '')) AS payee_display_name,
                   e.payment_account_id,
                   COALESCE(a.code || '  ' || a.name, '') AS payment_account_label,
                   e.payment_method, e.payment_date,
                   e.transaction_currency_code, e.total_amount,
                   e.source_purchase_order_number,
                   e.created_at, e.updated_at
              FROM expenses e
              LEFT JOIN vendors    v ON v.id = e.payee_id AND e.payee_kind = 'vendor'
              LEFT JOIN customers  c ON c.id = e.payee_id AND e.payee_kind = 'employee'
              LEFT JOIN accounts   a ON a.id = e.payment_account_id
             WHERE e.company_id = @company_id
            """;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            sql += " AND e.status = @status";
            command.Parameters.AddWithValue("status", filter.Status);
        }
        if (filter.PayeeId is { } payeeId)
        {
            sql += " AND e.payee_id = @payee_id";
            command.Parameters.AddWithValue("payee_id", payeeId);
        }
        if (filter.FromDate is { } fromDate)
        {
            sql += " AND e.payment_date >= @from_date";
            command.Parameters.Add("from_date", NpgsqlDbType.Date).Value = fromDate.ToDateTime(TimeOnly.MinValue);
        }
        if (filter.ToDate is { } toDate)
        {
            sql += " AND e.payment_date <= @to_date";
            command.Parameters.Add("to_date", NpgsqlDbType.Date).Value = toDate.ToDateTime(TimeOnly.MinValue);
        }
        sql += " ORDER BY e.payment_date DESC, e.created_at DESC;";
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(MapSummary(reader));
        }
        return rows;
    }

    public async Task<ExpenseRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid expenseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        ExpenseRecord? expense;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectColumns + " WHERE e.company_id = @company_id AND e.id = @id LIMIT 1;";
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("id", expenseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            expense = MapRecord(reader, lines: Array.Empty<ExpenseLineRecord>());
        }

        var lines = await ReadLinesAsync(connection, expenseId, cancellationToken).ConfigureAwait(false);
        return expense with { Lines = lines };
    }

    public async Task<ExpenseRecord> CreateAsync(
        CompanyId companyId,
        UserId createdByUserId,
        ExpenseUpsertInput input,
        CancellationToken cancellationToken)
    {
        var entityNumber = GenerateExpenseNumber();

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Look up base currency + payment account detail_type for cross-validation.
        var baseCurrencyCode = await ReadBaseCurrencyAsync(connection, companyId, cancellationToken).ConfigureAwait(false);
        var (paymentAccountCurrency, paymentAccountDetailType) =
            await ReadPaymentAccountAsync(connection, companyId, input.PaymentAccountId, cancellationToken).ConfigureAwait(false);

        var crossValidation = ExpensePaymentMethod.ValidateAgainstAccountDetailType(input.PaymentMethod, paymentAccountDetailType);
        if (crossValidation is not null)
        {
            throw new InvalidOperationException(crossValidation);
        }

        var (subtotal, discount, tax, total) = ComputeTotals(input);
        var transactionCurrency = input.TransactionCurrencyCode.Trim().ToUpperInvariant();
        var sameCurrency = string.Equals(transactionCurrency, baseCurrencyCode, StringComparison.Ordinal);
        var fxRate = input.FxRate ?? (sameCurrency ? 1m : 1m);
        var fxSource = sameCurrency ? "identity" : "manual";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid expenseId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO expenses (
                    company_id, expense_number, status,
                    payee_kind, payee_id, payee_name_freeform,
                    payment_account_id, payment_method, cheque_number, ref_no,
                    transaction_currency_code, base_currency_code, fx_rate, fx_source,
                    payment_date,
                    source_purchase_order_id, source_purchase_order_number,
                    tax_mode, discount_kind, discount_value,
                    subtotal_amount, discount_amount, tax_amount, total_amount,
                    memo, internal_note,
                    created_by_user_id
                )
                VALUES (
                    @company_id, @expense_number, 'posted',
                    @payee_kind, @payee_id, @payee_name_freeform,
                    @payment_account_id, @payment_method, @cheque_number, @ref_no,
                    @transaction_currency_code, @base_currency_code, @fx_rate, @fx_source,
                    @payment_date,
                    @source_purchase_order_id, @source_purchase_order_number,
                    @tax_mode, @discount_kind, @discount_value,
                    @subtotal_amount, @discount_amount, @tax_amount, @total_amount,
                    @memo, @internal_note,
                    @created_by_user_id
                )
                RETURNING id;
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("expense_number", entityNumber);
            command.Parameters.AddWithValue("payee_kind", input.PayeeKind);
            command.Parameters.AddWithValue("payee_id", (object?)input.PayeeId ?? DBNull.Value);
            command.Parameters.AddWithValue("payee_name_freeform", input.PayeeNameFreeform ?? string.Empty);
            command.Parameters.AddWithValue("payment_account_id", input.PaymentAccountId);
            command.Parameters.AddWithValue("payment_method", input.PaymentMethod);
            command.Parameters.AddWithValue("cheque_number", (object?)input.ChequeNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("ref_no", (object?)input.RefNo ?? DBNull.Value);
            command.Parameters.AddWithValue("transaction_currency_code", transactionCurrency);
            command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            command.Parameters.AddWithValue("fx_rate", fxRate);
            command.Parameters.AddWithValue("fx_source", fxSource);
            command.Parameters.Add("payment_date", NpgsqlDbType.Date).Value = input.PaymentDate.ToDateTime(TimeOnly.MinValue);
            command.Parameters.AddWithValue("source_purchase_order_id", (object?)input.SourcePurchaseOrderId ?? DBNull.Value);
            command.Parameters.AddWithValue("source_purchase_order_number", (object?)input.SourcePurchaseOrderNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("tax_mode", input.TaxMode);
            command.Parameters.AddWithValue("discount_kind", (object?)input.DiscountKind ?? DBNull.Value);
            command.Parameters.AddWithValue("discount_value", (object?)input.DiscountValue ?? DBNull.Value);
            command.Parameters.AddWithValue("subtotal_amount", subtotal);
            command.Parameters.AddWithValue("discount_amount", discount);
            command.Parameters.AddWithValue("tax_amount", tax);
            command.Parameters.AddWithValue("total_amount", total);
            command.Parameters.AddWithValue("memo", (object?)input.Memo ?? DBNull.Value);
            command.Parameters.AddWithValue("internal_note", (object?)input.InternalNote ?? DBNull.Value);
            command.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);

            expenseId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await InsertLinesAsync(connection, transaction, expenseId, input.Lines, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var saved = await GetByIdAsync(companyId, expenseId, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException("Expense insert returned no row.");
    }

    public async Task<ExpenseRecord?> VoidAsync(
        CompanyId companyId,
        Guid expenseId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE expenses
               SET status     = 'voided',
                   voided_at  = NOW(),
                   updated_at = NOW()
             WHERE company_id = @company_id AND id = @id AND status = 'posted'
            RETURNING id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", expenseId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT status FROM expenses WHERE company_id = @company_id AND id = @id LIMIT 1;";
            checkCmd.Parameters.AddWithValue("company_id", companyId.Value);
            checkCmd.Parameters.AddWithValue("id", expenseId);
            var status = (string?)await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (status is null) return null;
            throw new InvalidOperationException($"Expense in status '{status}' cannot be voided.");
        }
        return await GetByIdAsync(companyId, expenseId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ExpenseLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        Guid expenseId,
        CancellationToken cancellationToken)
    {
        var lines = new List<ExpenseLineRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, expense_id, sequence, service_date, item_id, expense_account_id,
                   description, quantity, unit_price, tax_code_id, line_total
              FROM expense_lines
             WHERE expense_id = @expense_id
             ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("expense_id", expenseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new ExpenseLineRecord(
                Id: reader.GetGuid(0),
                ExpenseId: reader.GetGuid(1),
                Sequence: reader.GetInt32(2),
                ServiceDate: reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                ItemId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                ExpenseAccountId: reader.GetGuid(5),
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
        Guid expenseId,
        IReadOnlyList<ExpenseLineInput> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO expense_lines (
                    expense_id, sequence, service_date,
                    item_id, expense_account_id, description,
                    quantity, unit_price, tax_code_id, line_total)
                VALUES (
                    @expense_id, @sequence, @service_date,
                    @item_id, @expense_account_id, @description,
                    @quantity, @unit_price, @tax_code_id, @line_total);
                """;
            command.Parameters.AddWithValue("expense_id", expenseId);
            command.Parameters.AddWithValue("sequence", line.Sequence);
            command.Parameters.Add("service_date", NpgsqlDbType.Date).Value =
                line.ServiceDate is { } svc ? svc.ToDateTime(TimeOnly.MinValue) : (object)DBNull.Value;
            command.Parameters.AddWithValue("item_id", (object?)line.ItemId ?? DBNull.Value);
            command.Parameters.AddWithValue("expense_account_id", line.ExpenseAccountId);
            command.Parameters.AddWithValue("description", line.Description ?? string.Empty);
            command.Parameters.AddWithValue("quantity", line.Quantity);
            command.Parameters.AddWithValue("unit_price", line.UnitPrice);
            command.Parameters.AddWithValue("tax_code_id", (object?)line.TaxCodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("line_total", Math.Round(line.Quantity * line.UnitPrice, 4));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static (decimal subtotal, decimal discount, decimal tax, decimal total) ComputeTotals(ExpenseUpsertInput input)
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

        decimal tax = 0m; // V1 — tax engine not yet wired.
        decimal total = string.Equals(input.TaxMode, "inclusive", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(subtotal - discount, 4)
            : Math.Round(subtotal - discount + tax, 4);

        return (subtotal, discount, tax, total);
    }

    private static async Task<string> ReadBaseCurrencyAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

    private static async Task<(string CurrencyCode, string? DetailType)> ReadPaymentAccountAsync(
        NpgsqlConnection connection,
        CompanyId companyId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(currency_code, ''), detail_type
              FROM accounts
             WHERE company_id = @company_id AND id = @id AND is_active = TRUE
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Payment account not found or inactive in this company.");
        }
        var currency = reader.GetString(0);
        var detailType = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (currency, detailType);
    }

    /// <summary>EXP{4-digit-year}{8-digit-random}.</summary>
    private static string GenerateExpenseNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, 100_000_000);
        return $"EXP{year:0000}{seed:00000000}";
    }

    private const string SelectColumns = """
        SELECT e.id, e.company_id, e.expense_number, e.status,
               e.payee_kind, e.payee_id, e.payee_name_freeform,
               e.payment_account_id,
               COALESCE(a.code || '  ' || a.name, '') AS payment_account_label,
               e.payment_method, e.cheque_number, e.ref_no,
               e.transaction_currency_code, e.base_currency_code, e.fx_rate, e.fx_source,
               e.payment_date,
               e.source_purchase_order_id, e.source_purchase_order_number,
               e.tax_mode, e.discount_kind, e.discount_value,
               e.subtotal_amount, e.discount_amount, e.tax_amount, e.total_amount,
               e.memo, e.internal_note,
               e.posted_journal_entry_id, e.voided_at,
               e.created_at, e.updated_at
          FROM expenses e
          LEFT JOIN accounts a ON a.id = e.payment_account_id
        """;

    private static ExpenseSummary MapSummary(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: CompanyId.Parse(reader.GetString(1)),
        ExpenseNumber: reader.GetString(2),
        Status: reader.GetString(3),
        PayeeKind: reader.GetString(4),
        PayeeId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
        PayeeDisplayName: reader.GetString(6),
        PaymentAccountId: reader.GetGuid(7),
        PaymentAccountLabel: reader.GetString(8),
        PaymentMethod: reader.GetString(9),
        PaymentDate: DateOnly.FromDateTime(reader.GetDateTime(10)),
        TransactionCurrencyCode: reader.GetString(11),
        TotalAmount: reader.GetDecimal(12),
        SourcePurchaseOrderNumber: reader.IsDBNull(13) ? null : reader.GetString(13),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(14),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15));

    private static ExpenseRecord MapRecord(NpgsqlDataReader reader, IReadOnlyList<ExpenseLineRecord> lines) => new(
        Id: reader.GetGuid(reader.GetOrdinal("id")),
        CompanyId: CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
        ExpenseNumber: reader.GetString(reader.GetOrdinal("expense_number")),
        Status: reader.GetString(reader.GetOrdinal("status")),
        PayeeKind: reader.GetString(reader.GetOrdinal("payee_kind")),
        PayeeId: ReadNullableGuid(reader, "payee_id"),
        PayeeNameFreeform: reader.GetString(reader.GetOrdinal("payee_name_freeform")),
        PaymentAccountId: reader.GetGuid(reader.GetOrdinal("payment_account_id")),
        PaymentAccountLabel: reader.GetString(reader.GetOrdinal("payment_account_label")),
        PaymentMethod: reader.GetString(reader.GetOrdinal("payment_method")),
        ChequeNumber: ReadNullableString(reader, "cheque_number"),
        RefNo: ReadNullableString(reader, "ref_no"),
        TransactionCurrencyCode: reader.GetString(reader.GetOrdinal("transaction_currency_code")),
        BaseCurrencyCode: reader.GetString(reader.GetOrdinal("base_currency_code")),
        FxRate: reader.GetDecimal(reader.GetOrdinal("fx_rate")),
        FxSource: reader.GetString(reader.GetOrdinal("fx_source")),
        PaymentDate: DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("payment_date"))),
        SourcePurchaseOrderId: ReadNullableGuid(reader, "source_purchase_order_id"),
        SourcePurchaseOrderNumber: ReadNullableString(reader, "source_purchase_order_number"),
        TaxMode: reader.GetString(reader.GetOrdinal("tax_mode")),
        DiscountKind: ReadNullableString(reader, "discount_kind"),
        DiscountValue: ReadNullableDecimal(reader, "discount_value"),
        SubtotalAmount: reader.GetDecimal(reader.GetOrdinal("subtotal_amount")),
        DiscountAmount: reader.GetDecimal(reader.GetOrdinal("discount_amount")),
        TaxAmount: reader.GetDecimal(reader.GetOrdinal("tax_amount")),
        TotalAmount: reader.GetDecimal(reader.GetOrdinal("total_amount")),
        Memo: ReadNullableString(reader, "memo"),
        InternalNote: ReadNullableString(reader, "internal_note"),
        PostedJournalEntryId: ReadNullableGuid(reader, "posted_journal_entry_id"),
        VoidedAt: ReadNullableDateTimeOffset(reader, "voided_at"),
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

    private static DateTimeOffset? ReadNullableDateTimeOffset(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }
}
