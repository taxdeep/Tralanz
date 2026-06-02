using Citus.Accounting.Application.Abstractions;
using Infrastructure.PostgreSQL.Numbering;
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

        // Copy A3 Phase 2: provenance audit. Same shape as the Expense
        // store's expense_copied row — see PostgreSqlExpenseStore for
        // the rationale. No FK on the bills row itself.
        if (input.CopiedFromBillId is { } sourceBillId)
        {
            await using var auditCommand = connection.CreateCommand();
            auditCommand.Transaction = transaction;
            auditCommand.CommandText = """
                INSERT INTO audit_logs (
                    company_id, actor_type, actor_id,
                    entity_type, entity_id, action, payload
                )
                VALUES (
                    @company_id, 'user', @actor_id,
                    'bill', @entity_id, 'bill_copied', @payload::jsonb
                );
                """;
            auditCommand.Parameters.AddWithValue("company_id", companyId.Value);
            auditCommand.Parameters.AddWithValue("actor_id", createdByUserId.Value);
            auditCommand.Parameters.AddWithValue("entity_id", billId);
            auditCommand.Parameters.AddWithValue(
                "payload",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    source_bill_id = sourceBillId,
                    new_bill_id = billId,
                    new_entity_number = entityNumber
                }));
            await auditCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        BillRecord bill;
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = SelectBillColumns + " WHERE b.company_id = @company_id AND b.id = @id FOR UPDATE OF b;";
            checkCmd.Parameters.AddWithValue("company_id", companyId.Value);
            checkCmd.Parameters.AddWithValue("id", billId);
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            bill = MapRecord(reader, lines: Array.Empty<BillLineRecord>());
        }

        if (string.Equals(bill.Status, BillStatus.Draft, StringComparison.Ordinal))
        {
            await MarkBillVoidedAsync(connection, transaction, companyId, billId, expectedStatus: BillStatus.Draft, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await GetByIdAsync(companyId, billId, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(bill.Status, BillStatus.Posted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Bill in status '{bill.Status}' cannot be voided.");
        }

        await EnsureBillHasNoSettlementApplicationsAsync(connection, transaction, companyId, billId, cancellationToken)
            .ConfigureAwait(false);

        var journal = await ReadPostedBillJournalAsync(connection, transaction, companyId, billId, cancellationToken)
            .ConfigureAwait(false);
        var originalLines = await ReadPostedJournalLinesForReversalAsync(
            connection,
            transaction,
            companyId,
            journal.Id,
            cancellationToken).ConfigureAwait(false);

        if (originalLines.Count == 0)
        {
            throw new InvalidOperationException("Posted bill journal entry has no lines to reverse.");
        }

        await InsertVoidedBillJournalAsync(
            connection,
            transaction,
            bill,
            journal,
            originalLines,
            cancellationToken).ConfigureAwait(false);

        await VoidBillOpenItemAsync(connection, transaction, companyId, billId, cancellationToken).ConfigureAwait(false);
        await MarkBillVoidedAsync(connection, transaction, companyId, billId, expectedStatus: BillStatus.Posted, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetByIdAsync(companyId, billId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkBillVoidedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billId,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE bills
               SET status     = 'voided',
                   updated_at = NOW()
             WHERE company_id = @company_id
               AND id = @id
               AND status = @expected_status;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", billId);
        command.Parameters.AddWithValue("expected_status", expectedStatus);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("Bill could not be marked voided.");
        }
    }

    private static async Task EnsureBillHasNoSettlementApplicationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
              FROM settlement_applications sa
              INNER JOIN ap_open_items oi
                ON oi.company_id = sa.company_id
               AND oi.id = sa.target_open_item_id
             WHERE sa.company_id = @company_id
               AND sa.target_open_item_type = 'ap_open_item'
               AND oi.source_type = 'bill'
               AND oi.source_id = @bill_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_id", billId);
        var applicationCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
        if (applicationCount > 0)
        {
            throw new InvalidOperationException(
                "This bill already has payment/application history. Void or reverse the related Pay Bills transaction before voiding the bill.");
        }
    }

    private static async Task<BillJournalHeaderSnapshot> ReadPostedBillJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,
                   transaction_currency_code,
                   base_currency_code,
                   exchange_rate,
                   exchange_rate_date,
                   exchange_rate_source,
                   created_by_user_id
              FROM journal_entries
             WHERE company_id = @company_id
               AND source_type = 'bill'
               AND source_id = @bill_id
               AND status = 'posted'
             ORDER BY posted_at DESC NULLS LAST, created_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_id", billId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Posted bill has no linked journal entry to reverse.");
        }

        return new BillJournalHeaderSnapshot(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            DateOnly.FromDateTime(reader.GetDateTime(4)),
            reader.GetString(5),
            reader.GetString(6));
    }

    private static async Task<IReadOnlyList<BillJournalLineSnapshot>> ReadPostedJournalLinesForReversalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_id,
                   description,
                   party_type,
                   party_id,
                   tx_debit,
                   tx_credit,
                   debit,
                   credit,
                   posting_role,
                   source_line_number
              FROM journal_entry_lines
             WHERE company_id = @company_id
               AND journal_entry_id = @journal_entry_id
             ORDER BY line_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

        var lines = new List<BillJournalLineSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new BillJournalLineSnapshot(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9)));
        }

        return lines;
    }

    private static async Task InsertVoidedBillJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BillRecord bill,
        BillJournalHeaderSnapshot journal,
        IReadOnlyList<BillJournalLineSnapshot> originalLines,
        CancellationToken cancellationToken)
    {
        var voidJournalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var journalDisplayNumber = await ReserveJournalDisplayNumberAsync(connection, transaction, bill.CompanyId, cancellationToken)
            .ConfigureAwait(false);
        var entityNumber = await ReserveEntityNumberAsync(connection, transaction, bill.CompanyId, bill.BillDate.Year, cancellationToken)
            .ConfigureAwait(false);
        var idempotencyKey = $"bill-void:{bill.Id:D}";

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
                  'bill_void', @source_id,
                  @transaction_currency_code, @base_currency_code,
                  @exchange_rate, @exchange_rate_date, @exchange_rate_source,
                  NULL,
                  @total_tx_debit, @total_tx_credit, @total_debit, @total_credit,
                  @posting_run_id, @idempotency_key, @posted_at, @created_by_user_id, NOW()
                );
                """;
            insertEntryCommand.Parameters.AddWithValue("id", voidJournalEntryId);
            insertEntryCommand.Parameters.AddWithValue("company_id", bill.CompanyId.Value);
            insertEntryCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertEntryCommand.Parameters.AddWithValue("display_number", journalDisplayNumber);
            insertEntryCommand.Parameters.AddWithValue("source_id", bill.Id);
            insertEntryCommand.Parameters.AddWithValue("transaction_currency_code", journal.TransactionCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("base_currency_code", journal.BaseCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate", RoundRate(journal.ExchangeRate));
            insertEntryCommand.Parameters.Add("exchange_rate_date", NpgsqlDbType.Date).Value =
                journal.ExchangeRateDate.ToDateTime(TimeOnly.MinValue);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate_source", journal.ExchangeRateSource);
            insertEntryCommand.Parameters.AddWithValue("total_tx_debit", RoundTx(originalLines.Sum(static line => line.TxCredit)));
            insertEntryCommand.Parameters.AddWithValue("total_tx_credit", RoundTx(originalLines.Sum(static line => line.TxDebit)));
            insertEntryCommand.Parameters.AddWithValue("total_debit", RoundBase(originalLines.Sum(static line => line.Credit)));
            insertEntryCommand.Parameters.AddWithValue("total_credit", RoundBase(originalLines.Sum(static line => line.Debit)));
            insertEntryCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            insertEntryCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insertEntryCommand.Parameters.AddWithValue("posted_at", postedAt);
            insertEntryCommand.Parameters.AddWithValue("created_by_user_id", journal.CreatedByUserId);
            await insertEntryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var lineNumber = 1;
        foreach (var originalLine in originalLines)
        {
            await InsertJournalAndLedgerLineAsync(
                connection,
                transaction,
                bill.CompanyId,
                voidJournalEntryId,
                lineNumber++,
                originalLine.AccountId,
                originalLine.PartyType,
                originalLine.PartyId,
                $"Void {originalLine.Description}",
                journal.TransactionCurrencyCode,
                txDebit: originalLine.TxCredit,
                txCredit: originalLine.TxDebit,
                debit: originalLine.Credit,
                credit: originalLine.Debit,
                postingDate: bill.BillDate,
                postingRole: $"void:{originalLine.PostingRole}",
                sourceLineNumber: originalLine.SourceLineNumber,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task VoidBillOpenItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ap_open_items
               SET status = 'voided',
                   open_amount_tx = 0,
                   open_amount_base = 0,
                   updated_at = NOW()
             WHERE company_id = @company_id
               AND source_type = 'bill'
               AND source_id = @bill_id
               AND status <> 'voided';
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bill_id", billId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken) =>
        await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            $"entity-number:all:{year}",
            $"EN{year}",
            5,
            1,
            cancellationToken).ConfigureAwait(false);

    private static decimal RoundTx(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static decimal RoundBase(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    private static decimal RoundRate(decimal value) =>
        Math.Round(value, 10, MidpointRounding.ToEven);

    private sealed record BillJournalHeaderSnapshot(
        Guid Id,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal ExchangeRate,
        DateOnly ExchangeRateDate,
        string ExchangeRateSource,
        string CreatedByUserId);

    private sealed record BillJournalLineSnapshot(
        Guid AccountId,
        string Description,
        string? PartyType,
        Guid? PartyId,
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit,
        string PostingRole,
        int? SourceLineNumber);

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

        var recoverableTaxCodeIds = await ReadRecoverablePurchaseTaxCodeIdsAsync(
            connection,
            transaction,
            companyId,
            lines,
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isTaxRecoverable =
                line.TaxCodeId is { } taxCodeId &&
                recoverableTaxCodeIds.Contains(taxCodeId);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO bill_lines (
                    company_id, bill_id, line_number, expense_account_id, description,
                    line_amount, tax_code_id, tax_amount, is_tax_recoverable, task_id)
                VALUES (
                    @company_id, @bill_id, @line_number, @expense_account_id, @description,
                    @line_amount, @tax_code_id, @tax_amount, @is_tax_recoverable, @task_id);
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("bill_id", billId);
            command.Parameters.AddWithValue("line_number", line.LineNumber);
            command.Parameters.AddWithValue("expense_account_id", line.ExpenseAccountId);
            command.Parameters.AddWithValue("description", line.Description ?? string.Empty);
            command.Parameters.AddWithValue("line_amount", line.LineAmount);
            command.Parameters.AddWithValue("tax_code_id", (object?)line.TaxCodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("tax_amount", line.TaxAmount);
            command.Parameters.AddWithValue("is_tax_recoverable", isTaxRecoverable);
            // task_id column added by PostgresTaskLinkSchemaInitializer
            // (Batch 8). Validator already rejected billed / canceled /
            // cross-company task ids at the route layer.
            command.Parameters.AddWithValue("task_id", (object?)line.TaskId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlySet<Guid>> ReadRecoverablePurchaseTaxCodeIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        IReadOnlyList<BillLineInput> lines,
        CancellationToken cancellationToken)
    {
        var taxCodeIds = lines
            .Select(static line => line.TaxCodeId)
            .Where(static id => id is not null)
            .Select(static id => id!.Value)
            .Distinct()
            .ToArray();

        if (taxCodeIds.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var recoverableTaxCodeIds = new HashSet<Guid>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT tc.id,
                   tc.code,
                   tc.applies_to,
                   tc.is_active,
                   COALESCE(
                       BOOL_OR(
                           COALESCE(tcc.applies_to, tc.applies_to) IN ('purchase', 'both')
                           AND COALESCE(tcc.recoverability_override, stc.recoverability) = 'recoverable'),
                       tc.is_recoverable_on_purchase
                       OR tc.recoverability_mode <> 'none') AS is_recoverable_on_purchase
              FROM tax_codes tc
            LEFT JOIN sales_tax_code_components tcc
                ON tcc.company_id = tc.company_id::text
               AND tcc.tax_code_id = tc.id
            LEFT JOIN sales_tax_components stc
                ON stc.company_id = tc.company_id::text
               AND stc.id = tcc.tax_component_id
             WHERE tc.company_id = @company_id
               AND tc.id = ANY(@ids)
            GROUP BY tc.id,
                   tc.code,
                   tc.applies_to,
                   tc.is_active,
                   tc.is_recoverable_on_purchase,
                   tc.recoverability_mode;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = taxCodeIds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found++;
            var code = reader.GetString(1);
            var appliesTo = reader.GetString(2);
            var isActive = reader.GetBoolean(3);
            var isRecoverableOnPurchase = reader.GetBoolean(4);
            if (!isActive)
            {
                throw new InvalidOperationException($"Tax code '{code}' is inactive and cannot be used on a bill.");
            }
            if (appliesTo is not (TaxCodeAppliesTo.Purchase or TaxCodeAppliesTo.Both))
            {
                throw new InvalidOperationException($"Tax code '{code}' is not available for purchases.");
            }
            if (isRecoverableOnPurchase)
            {
                recoverableTaxCodeIds.Add(reader.GetGuid(0));
            }
        }

        if (found != taxCodeIds.Length)
        {
            throw new InvalidOperationException("One or more tax codes are not available in the active company.");
        }

        return recoverableTaxCodeIds;
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
