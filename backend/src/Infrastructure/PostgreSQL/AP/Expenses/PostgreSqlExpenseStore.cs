using Modules.AP.Expenses;
using Modules.AP;
using Npgsql;
using NpgsqlTypes;
using Infrastructure.PostgreSQL.Numbering;

namespace Infrastructure.PostgreSQL.AP.Expenses;

/// <summary>
/// PostgreSQL backing for <see cref="IExpenseStore"/>. Owns the
/// <c>expenses</c> + <c>expense_lines</c> tables. EnsureSchemaAsync
/// verifies the migration-installed schema instead of applying DDL or
/// data migrations from the application process.
/// </summary>
public sealed class PostgreSqlExpenseStore(PostgreSqlConnectionFactory connections) : IExpenseStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await PostgreSqlSchemaChecks.EnsureTableColumnsAsync(
            connections,
            "expenses",
            new[]
            {
                "id",
                "company_id",
                "expense_number",
                "status",
                "payee_kind",
                "payee_id",
                "payee_name_freeform",
                "payment_account_id",
                "payment_method",
                "cheque_number",
                "ref_no",
                "transaction_currency_code",
                "base_currency_code",
                "fx_rate",
                "fx_source",
                "payment_date",
                "source_purchase_order_id",
                "source_purchase_order_number",
                "tax_mode",
                "discount_kind",
                "discount_value",
                "subtotal_amount",
                "discount_amount",
                "tax_amount",
                "total_amount",
                "memo",
                "internal_note",
                "posted_journal_entry_id",
                "voided_at",
                "created_by_user_id",
                "created_at",
                "updated_at"
            },
            "Expense schema has not been installed. Apply database migrations before using expenses.",
            cancellationToken).ConfigureAwait(false);

        await PostgreSqlSchemaChecks.EnsureTableColumnsAsync(
            connections,
            "expense_lines",
            new[]
            {
                "id",
                "expense_id",
                "sequence",
                "service_date",
                "item_id",
                "expense_account_id",
                "description",
                "quantity",
                "unit_price",
                "tax_code_id",
                "line_total"
            },
            "Expense line schema has not been installed. Apply database migrations before using expenses.",
            cancellationToken).ConfigureAwait(false);
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
        var fxRate = FxRatePostingPolicy.ResolveTransactionToBaseRate(
            input.FxRate,
            transactionCurrency,
            baseCurrencyCode,
            "expense");
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

        var activeLines = input.Lines
            .Where(static line => line.ExpenseAccountId != Guid.Empty && (line.Quantity > 0m || line.UnitPrice > 0m))
            .ToArray();

        await InsertLinesAsync(connection, transaction, expenseId, activeLines, cancellationToken).ConfigureAwait(false);
        var journalEntryId = await InsertPostedExpenseJournalAsync(
            connection,
            transaction,
            companyId,
            createdByUserId,
            expenseId,
            entityNumber,
            input,
            activeLines,
            transactionCurrency,
            baseCurrencyCode,
            fxRate,
            total,
            discount,
            cancellationToken).ConfigureAwait(false);

        await using (var updateJournalCommand = connection.CreateCommand())
        {
            updateJournalCommand.Transaction = transaction;
            updateJournalCommand.CommandText = """
                UPDATE expenses
                   SET posted_journal_entry_id = @journal_entry_id,
                       updated_at = NOW()
                 WHERE company_id = @company_id AND id = @id;
                """;
            updateJournalCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            updateJournalCommand.Parameters.AddWithValue("company_id", companyId.Value);
            updateJournalCommand.Parameters.AddWithValue("id", expenseId);
            await updateJournalCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        ExpenseRecord expense;
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = SelectColumns + " WHERE e.company_id = @company_id AND e.id = @id FOR UPDATE OF e;";
            checkCmd.Parameters.AddWithValue("company_id", companyId.Value);
            checkCmd.Parameters.AddWithValue("id", expenseId);
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            expense = MapRecord(reader, lines: Array.Empty<ExpenseLineRecord>());
        }

        if (!string.Equals(expense.Status, ExpenseStatus.Posted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expense in status '{expense.Status}' cannot be voided.");
        }

        var lines = await ReadLinesAsync(connection, expenseId, cancellationToken, transaction).ConfigureAwait(false);
        expense = expense with { Lines = lines };

        if (expense.PostedJournalEntryId is { })
        {
            await InsertVoidedExpenseJournalAsync(
                connection,
                transaction,
                expense,
                cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE expenses
                   SET status     = 'voided',
                       voided_at  = NOW(),
                       updated_at = NOW()
                 WHERE company_id = @company_id AND id = @id AND status = 'posted';
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("id", expenseId);

            var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException("Expense could not be marked voided.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetByIdAsync(companyId, expenseId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ExpenseLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection,
        Guid expenseId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        var lines = new List<ExpenseLineRecord>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static async Task<Guid> InsertPostedExpenseJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        Guid expenseId,
        string expenseNumber,
        ExpenseUpsertInput input,
        IReadOnlyList<ExpenseLineInput> activeLines,
        string transactionCurrency,
        string baseCurrencyCode,
        decimal fxRate,
        decimal totalTx,
        decimal discountTx,
        CancellationToken cancellationToken)
    {
        if (activeLines.Count == 0)
        {
            throw new InvalidOperationException("At least one active expense line is required.");
        }

        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var totalBase = RoundBase(totalTx * fxRate);
        var journalDisplayNumber = await ReserveJournalDisplayNumberAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
        var entityNumber = await ReserveEntityNumberAsync(connection, transaction, companyId, input.PaymentDate.Year, cancellationToken).ConfigureAwait(false);
        var idempotencyKey = $"expense:{expenseId:D}";

        await using (var insertEntryCommand = connection.CreateCommand())
        {
            insertEntryCommand.Transaction = transaction;
            insertEntryCommand.CommandText = """
                INSERT INTO journal_entries (
                  id, company_id, entity_number, display_number, status,
                  source_type, source_id,
                  transaction_currency_code, base_currency_code,
                  exchange_rate, exchange_rate_date, exchange_rate_source,
                  fx_rate_snapshot_id,
                  total_tx_debit, total_tx_credit, total_debit, total_credit,
                  posting_run_id, idempotency_key, posted_at, created_by_user_id, created_at
                )
                VALUES (
                  @id, @company_id, @entity_number, @display_number, 'posted',
                  'expense', @source_id,
                  @transaction_currency_code, @base_currency_code,
                  @exchange_rate, @exchange_rate_date, @exchange_rate_source,
                  NULL,
                  @total_tx_debit, @total_tx_credit, @total_debit, @total_credit,
                  @posting_run_id, @idempotency_key, @posted_at, @created_by_user_id, NOW()
                )
                ON CONFLICT (company_id, idempotency_key) DO NOTHING;
                """;
            insertEntryCommand.Parameters.AddWithValue("id", journalEntryId);
            insertEntryCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertEntryCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertEntryCommand.Parameters.AddWithValue("display_number", journalDisplayNumber);
            insertEntryCommand.Parameters.AddWithValue("source_id", expenseId);
            insertEntryCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrency);
            insertEntryCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate", RoundRate(fxRate));
            insertEntryCommand.Parameters.Add("exchange_rate_date", NpgsqlDbType.Date).Value = input.PaymentDate.ToDateTime(TimeOnly.MinValue);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate_source", string.Equals(transactionCurrency, baseCurrencyCode, StringComparison.Ordinal) ? "identity" : "manual");
            insertEntryCommand.Parameters.AddWithValue("total_tx_debit", RoundTx(totalTx));
            insertEntryCommand.Parameters.AddWithValue("total_tx_credit", RoundTx(totalTx));
            insertEntryCommand.Parameters.AddWithValue("total_debit", totalBase);
            insertEntryCommand.Parameters.AddWithValue("total_credit", totalBase);
            insertEntryCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            insertEntryCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insertEntryCommand.Parameters.AddWithValue("posted_at", postedAt);
            insertEntryCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
            var insertedRows = await insertEntryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (insertedRows != 1)
            {
                return await ReadExistingJournalEntryIdAsync(connection, transaction, companyId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
        }

        var lineAllocations = AllocateExpenseLines(activeLines, totalTx, discountTx, fxRate);
        var lineNumber = 1;
        foreach (var allocation in lineAllocations)
        {
            await InsertJournalAndLedgerLineAsync(
                connection,
                transaction,
                companyId,
                journalEntryId,
                lineNumber++,
                allocation.Line.ExpenseAccountId,
                input.PayeeKind,
                input.PayeeId,
                allocation.Description(expenseNumber),
                transactionCurrency,
                txDebit: allocation.TxAmount,
                txCredit: 0m,
                debit: allocation.BaseAmount,
                credit: 0m,
                postingDate: input.PaymentDate,
                postingRole: "source_line:expense",
                sourceLineNumber: allocation.Line.Sequence,
                cancellationToken).ConfigureAwait(false);
        }

        await InsertJournalAndLedgerLineAsync(
            connection,
            transaction,
            companyId,
            journalEntryId,
            lineNumber,
            input.PaymentAccountId,
            input.PayeeKind,
            input.PayeeId,
            $"Payment for expense {expenseNumber}",
            transactionCurrency,
            txDebit: 0m,
            txCredit: totalTx,
            debit: 0m,
            credit: totalBase,
            postingDate: input.PaymentDate,
            postingRole: "control:payment_account",
            sourceLineNumber: null,
            cancellationToken).ConfigureAwait(false);

        return journalEntryId;
    }

    private static async Task InsertVoidedExpenseJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExpenseRecord expense,
        CancellationToken cancellationToken)
    {
        if (expense.Lines.Count == 0)
        {
            throw new InvalidOperationException("Expense has no lines to reverse.");
        }

        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var journalDisplayNumber = await ReserveJournalDisplayNumberAsync(connection, transaction, expense.CompanyId, cancellationToken).ConfigureAwait(false);
        var entityNumber = await ReserveEntityNumberAsync(connection, transaction, expense.CompanyId, expense.PaymentDate.Year, cancellationToken).ConfigureAwait(false);
        var idempotencyKey = $"expense-void:{expense.Id:D}";
        var totalTx = RoundTx(expense.TotalAmount);
        var totalBase = RoundBase(expense.TotalAmount * expense.FxRate);
        var createdByUserId = await ReadJournalCreatedByUserIdAsync(
            connection,
            transaction,
            expense.CompanyId,
            expense.PostedJournalEntryId!.Value,
            cancellationToken).ConfigureAwait(false);

        await using (var insertEntryCommand = connection.CreateCommand())
        {
            insertEntryCommand.Transaction = transaction;
            insertEntryCommand.CommandText = """
                INSERT INTO journal_entries (
                  id, company_id, entity_number, display_number, status,
                  source_type, source_id,
                  transaction_currency_code, base_currency_code,
                  exchange_rate, exchange_rate_date, exchange_rate_source,
                  fx_rate_snapshot_id,
                  total_tx_debit, total_tx_credit, total_debit, total_credit,
                  posting_run_id, idempotency_key, posted_at, created_by_user_id, created_at
                )
                VALUES (
                  @id, @company_id, @entity_number, @display_number, 'posted',
                  'expense_void', @source_id,
                  @transaction_currency_code, @base_currency_code,
                  @exchange_rate, @exchange_rate_date, @exchange_rate_source,
                  NULL,
                  @total_tx_debit, @total_tx_credit, @total_debit, @total_credit,
                  @posting_run_id, @idempotency_key, @posted_at, @created_by_user_id, NOW()
                )
                ON CONFLICT (company_id, idempotency_key) DO NOTHING;
                """;
            insertEntryCommand.Parameters.AddWithValue("id", journalEntryId);
            insertEntryCommand.Parameters.AddWithValue("company_id", expense.CompanyId.Value);
            insertEntryCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertEntryCommand.Parameters.AddWithValue("display_number", journalDisplayNumber);
            insertEntryCommand.Parameters.AddWithValue("source_id", expense.Id);
            insertEntryCommand.Parameters.AddWithValue("transaction_currency_code", expense.TransactionCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("base_currency_code", expense.BaseCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate", RoundRate(expense.FxRate));
            insertEntryCommand.Parameters.Add("exchange_rate_date", NpgsqlDbType.Date).Value = expense.PaymentDate.ToDateTime(TimeOnly.MinValue);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate_source", expense.FxSource);
            insertEntryCommand.Parameters.AddWithValue("total_tx_debit", totalTx);
            insertEntryCommand.Parameters.AddWithValue("total_tx_credit", totalTx);
            insertEntryCommand.Parameters.AddWithValue("total_debit", totalBase);
            insertEntryCommand.Parameters.AddWithValue("total_credit", totalBase);
            insertEntryCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            insertEntryCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insertEntryCommand.Parameters.AddWithValue("posted_at", postedAt);
            insertEntryCommand.Parameters.AddWithValue("created_by_user_id", createdByUserId);
            var insertedRows = await insertEntryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (insertedRows != 1)
            {
                return;
            }
        }

        await InsertJournalAndLedgerLineAsync(
            connection,
            transaction,
            expense.CompanyId,
            journalEntryId,
            lineNumber: 1,
            expense.PaymentAccountId,
            expense.PayeeKind,
            expense.PayeeId,
            $"Void payment for expense {expense.ExpenseNumber}",
            expense.TransactionCurrencyCode,
            txDebit: totalTx,
            txCredit: 0m,
            debit: totalBase,
            credit: 0m,
            postingDate: expense.PaymentDate,
            postingRole: "void:payment_account",
            sourceLineNumber: null,
            cancellationToken).ConfigureAwait(false);

        var activeLines = expense.Lines
            .Where(static line => line.LineTotal > 0m)
            .Select(static line => new ExpenseLineInput(
                line.Sequence,
                line.ServiceDate,
                line.ItemId,
                line.ExpenseAccountId,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.TaxCodeId))
            .ToArray();
        var allocations = AllocateExpenseLines(activeLines, expense.TotalAmount, expense.DiscountAmount, expense.FxRate);
        var lineNumber = 2;
        foreach (var allocation in allocations)
        {
            await InsertJournalAndLedgerLineAsync(
                connection,
                transaction,
                expense.CompanyId,
                journalEntryId,
                lineNumber++,
                allocation.Line.ExpenseAccountId,
                expense.PayeeKind,
                expense.PayeeId,
                $"Void {allocation.Description(expense.ExpenseNumber)}",
                expense.TransactionCurrencyCode,
                txDebit: 0m,
                txCredit: allocation.TxAmount,
                debit: 0m,
                credit: allocation.BaseAmount,
                postingDate: expense.PaymentDate,
                postingRole: "void:expense",
                sourceLineNumber: allocation.Line.Sequence,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertJournalAndLedgerLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        int lineNumber,
        Guid accountId,
        string? partyType,
        Guid? partyId,
        string description,
        string transactionCurrencyCode,
        decimal txDebit,
        decimal txCredit,
        decimal debit,
        decimal credit,
        DateOnly postingDate,
        string postingRole,
        int? sourceLineNumber,
        CancellationToken cancellationToken)
    {
        var journalEntryLineId = Guid.NewGuid();

        await using (var insertLineCommand = connection.CreateCommand())
        {
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText = """
                INSERT INTO journal_entry_lines (
                  id, company_id, journal_entry_id, line_number,
                  account_id, description, party_type, party_id,
                  tx_debit, tx_credit, debit, credit,
                  tax_component_type, control_role, posting_role, source_line_number,
                  created_at
                )
                VALUES (
                  @id, @company_id, @journal_entry_id, @line_number,
                  @account_id, @description, @party_type, @party_id,
                  @tx_debit, @tx_credit, @debit, @credit,
                  NULL, NULL, @posting_role, @source_line_number,
                  NOW()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", journalEntryLineId);
            insertLineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertLineCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            insertLineCommand.Parameters.AddWithValue("line_number", lineNumber);
            insertLineCommand.Parameters.AddWithValue("account_id", accountId);
            insertLineCommand.Parameters.AddWithValue("description", description);
            insertLineCommand.Parameters.AddWithValue("party_type", string.IsNullOrWhiteSpace(partyType) ? DBNull.Value : partyType);
            insertLineCommand.Parameters.AddWithValue("party_id", (object?)partyId ?? DBNull.Value);
            insertLineCommand.Parameters.AddWithValue("tx_debit", RoundTx(txDebit));
            insertLineCommand.Parameters.AddWithValue("tx_credit", RoundTx(txCredit));
            insertLineCommand.Parameters.AddWithValue("debit", RoundBase(debit));
            insertLineCommand.Parameters.AddWithValue("credit", RoundBase(credit));
            insertLineCommand.Parameters.AddWithValue("posting_role", postingRole);
            insertLineCommand.Parameters.AddWithValue("source_line_number", (object?)sourceLineNumber ?? DBNull.Value);
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insertLedgerCommand = connection.CreateCommand();
        insertLedgerCommand.Transaction = transaction;
        insertLedgerCommand.CommandText = """
            INSERT INTO ledger_entries (
              id, company_id, journal_entry_id, journal_entry_line_id,
              posting_date, account_id, debit, credit,
              transaction_currency_code, tx_debit, tx_credit,
              created_at
            )
            VALUES (
              @id, @company_id, @journal_entry_id, @journal_entry_line_id,
              @posting_date, @account_id, @debit, @credit,
              @transaction_currency_code, @tx_debit, @tx_credit,
              NOW()
            );
            """;
        insertLedgerCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        insertLedgerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertLedgerCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        insertLedgerCommand.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
        insertLedgerCommand.Parameters.AddWithValue("posting_date", postingDate);
        insertLedgerCommand.Parameters.AddWithValue("account_id", accountId);
        insertLedgerCommand.Parameters.AddWithValue("debit", RoundBase(debit));
        insertLedgerCommand.Parameters.AddWithValue("credit", RoundBase(credit));
        insertLedgerCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        insertLedgerCommand.Parameters.AddWithValue("tx_debit", RoundTx(txDebit));
        insertLedgerCommand.Parameters.AddWithValue("tx_credit", RoundTx(txCredit));
        await insertLedgerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<ExpenseLineAllocation> AllocateExpenseLines(
        IReadOnlyList<ExpenseLineInput> activeLines,
        decimal totalTx,
        decimal discountTx,
        decimal fxRate)
    {
        var subtotalTx = activeLines.Sum(static line => Math.Round(line.Quantity * line.UnitPrice, 4));
        if (subtotalTx <= 0m)
        {
            throw new InvalidOperationException("Expense total must be greater than zero.");
        }

        var allocations = new List<ExpenseLineAllocation>(activeLines.Count);
        var remainingTx = RoundTx(totalTx);
        var remainingBase = RoundBase(totalTx * fxRate);

        for (var i = 0; i < activeLines.Count; i++)
        {
            var line = activeLines[i];
            var lineGross = Math.Round(line.Quantity * line.UnitPrice, 4);
            decimal lineTx;
            decimal lineBase;
            if (i == activeLines.Count - 1)
            {
                lineTx = remainingTx;
                lineBase = remainingBase;
            }
            else
            {
                var lineDiscount = discountTx == 0m ? 0m : Math.Round(discountTx * (lineGross / subtotalTx), 6, MidpointRounding.ToEven);
                lineTx = RoundTx(lineGross - lineDiscount);
                lineBase = RoundBase(lineTx * fxRate);
                remainingTx -= lineTx;
                remainingBase -= lineBase;
            }

            if (lineTx <= 0m)
            {
                continue;
            }

            allocations.Add(new ExpenseLineAllocation(
                line,
                lineTx,
                lineBase,
                number => $"Expense {number} line {line.Sequence}: {line.Description}".TrimEnd()));
        }

        if (allocations.Count == 0)
        {
            throw new InvalidOperationException("Expense total must be greater than zero.");
        }

        return allocations;
    }

    private static async Task<Guid> ReadExistingJournalEntryIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
              FROM journal_entries
             WHERE company_id = @company_id
               AND idempotency_key = @idempotency_key
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Posted expense journal entry could not be resolved."));
    }

    private static async Task<string> ReserveJournalDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var seedCommand = connection.CreateCommand();
        seedCommand.Transaction = transaction;
        seedCommand.CommandText = """
            SELECT COALESCE(
                MAX(
                    CASE
                        WHEN display_number ~ '^JE-[0-9]+$'
                        THEN substring(display_number from 4)::bigint
                        ELSE NULL
                    END),
                0) + 1
              FROM journal_entries
             WHERE company_id = @company_id;
            """;
        seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
        var seedNumber = Convert.ToInt64(await seedCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 1L);

        return await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            "journal-entry-display",
            "JE-",
            6,
            seedNumber,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        return await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            $"entity-number:all:{year}",
            $"EN{year}",
            5,
            1,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadJournalCreatedByUserIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT created_by_user_id
              FROM journal_entries
             WHERE company_id = @company_id
               AND id = @journal_entry_id
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Posted expense journal entry could not be found for reversal."));
    }

    private static decimal RoundTx(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static decimal RoundBase(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    private static decimal RoundRate(decimal value) =>
        Math.Round(value, 10, MidpointRounding.ToEven);

    private sealed record ExpenseLineAllocation(
        ExpenseLineInput Line,
        decimal TxAmount,
        decimal BaseAmount,
        Func<string, string> Description);

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
