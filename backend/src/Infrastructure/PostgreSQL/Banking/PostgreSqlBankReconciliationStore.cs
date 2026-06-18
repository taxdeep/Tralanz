using System.Data;
using Citus.Accounting.Application.Reconciliation;
using Citus.Accounting.Domain.Common;
using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Banking;

public sealed class PostgreSqlBankReconciliationStore(PostgreSqlConnectionFactory connections) : IBankReconciliationStore
{
    public async Task<IReadOnlyList<BankReconciliationLedgerEntry>> ListUnreconciledLedgerEntriesAsync(
        CompanyId companyId,
        Guid bankAccountId,
        DateOnly statementDate,
        CancellationToken cancellationToken)
    {
        var items = new List<BankReconciliationLedgerEntry>();

        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureReconcilableAccountAsync(connection, null, companyId, bankAccountId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = LedgerEntrySelectSql(
            """
            le.company_id = @company_id
            and le.account_id = @bank_account_id
            and le.posting_date <= @statement_date
            and je.status in ('posted', 'voided', 'reversed')
            and le.reconciliation_id is null
            and le.reconciliation_draft_id is null
            and (le.tx_debit <> 0 or le.tx_credit <> 0)
            order by le.posting_date asc, le.created_at asc, le.id asc
            limit 500;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);
        command.Parameters.AddWithValue("statement_date", statementDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapLedgerEntry(reader));
        }

        return items;
    }

    public async Task<BankReconciliationSummary> CompleteAsync(
        CompanyId companyId,
        UserId completedByUserId,
        BankReconciliationCompleteInput input,
        CancellationToken cancellationToken)
    {
        if (input.BankAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Statement account is required.");
        }
        if (input.StatementDate == default)
        {
            throw new InvalidOperationException("Statement date is required.");
        }
        if (input.LedgerEntryIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one ledger entry to reconcile.");
        }

        var distinctLedgerEntryIds = input.LedgerEntryIds.Distinct().ToArray();
        if (distinctLedgerEntryIds.Length != input.LedgerEntryIds.Count)
        {
            throw new InvalidOperationException("The reconciliation request contains duplicate ledger entries.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        try
        {
            await EnsureReconcilableAccountAsync(connection, transaction, companyId, input.BankAccountId, cancellationToken);
            var lockedEntries = await LoadSelectedLedgerEntriesForUpdateAsync(
                connection,
                transaction,
                companyId,
                input.BankAccountId,
                input.StatementDate,
                distinctLedgerEntryIds,
                cancellationToken);

            if (lockedEntries.Count != distinctLedgerEntryIds.Length)
            {
                throw new InvalidOperationException(
                    "One or more selected ledger entries are already reconciled, do not belong to this bank account, are after the statement date, or are not posted.");
            }

            var moneyDecimals = await LoadMoneyDecimalsAsync(connection, transaction, companyId, cancellationToken);
            var calculation = BankReconciliationPolicy.Calculate(
                input.OpeningBalance,
                input.EndingBalance,
                lockedEntries);
            if (!BankReconciliationPolicy.IsZeroDifference(calculation.Difference, moneyDecimals))
            {
                throw new InvalidOperationException(
                    $"Reconciliation difference must be zero before completion. Current difference is {calculation.Difference:N2}.");
            }

            var reconciliationId = Guid.NewGuid();
            var completedAt = DateTimeOffset.UtcNow;

            await InsertReconciliationAsync(
                connection,
                transaction,
                reconciliationId,
                companyId,
                completedByUserId,
                input,
                calculation,
                completedAt,
                lockedEntries.Count,
                cancellationToken);

            await InsertReconciliationLinesAsync(
                connection,
                transaction,
                reconciliationId,
                companyId,
                lockedEntries,
                cancellationToken);

            // R-1: also stamp ledger_entries.reconciliation_id so the
            // line-level cleared state stays in sync with the
            // snapshot table. Without this, the candidate query in
            // ListUnreconciledLedgerEntriesAsync (which now filters on
            // reconciliation_id IS NULL after R-1) would re-surface
            // the same entries for double-reconciliation. Equivalent
            // to the flip-step in CompleteDraftAsync.
            var ledgerEntryIds = lockedEntries.Select(e => e.LedgerEntryId).ToArray();
            await using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText =
                    """
                    update ledger_entries
                       set reconciliation_id = @reconciliation_id
                     where company_id = @company_id
                       and id = any(@ledger_entry_ids)
                       and reconciliation_id is null;
                    """;
                stamp.Parameters.AddWithValue("reconciliation_id", reconciliationId);
                stamp.Parameters.AddWithValue("company_id", companyId.Value);
                stamp.Parameters.AddWithValue("ledger_entry_ids", ledgerEntryIds);
                await stamp.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new BankReconciliationSummary(
                reconciliationId,
                input.BankAccountId,
                input.StatementDate,
                calculation.OpeningBalance,
                calculation.StatementEndingBalance,
                calculation.ClearedIncrease,
                calculation.ClearedDecrease,
                calculation.CalculatedEndingBalance,
                calculation.Difference,
                lockedEntries.Count,
                completedByUserId,
                completedAt);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureReconcilableAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select root_type, detail_type, is_active
            from accounts
            where company_id = @company_id
              and id = @bank_account_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Statement account was not found in the active company.");
        }

        var rootType = reader.GetString(reader.GetOrdinal("root_type"));
        var detailType = reader.GetString(reader.GetOrdinal("detail_type"));
        var isActive = reader.GetBoolean(reader.GetOrdinal("is_active"));
        var isReconcilable =
            (rootType == "asset" && detailType is "bank" or "cash") ||
            (rootType == "liability" && detailType == "credit_card");
        if (!isActive || !isReconcilable)
        {
            throw new InvalidOperationException("Reconciliation supports active bank, cash, and credit card accounts.");
        }
    }

    private static async Task<IReadOnlyList<BankReconciliationLedgerEntry>> LoadSelectedLedgerEntriesForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid bankAccountId,
        DateOnly statementDate,
        Guid[] ledgerEntryIds,
        CancellationToken cancellationToken)
    {
        var items = new List<BankReconciliationLedgerEntry>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LedgerEntrySelectSql(
            """
            le.company_id = @company_id
            and le.account_id = @bank_account_id
            and le.posting_date <= @statement_date
            and le.id = any(@ledger_entry_ids)
            and je.status in ('posted', 'voided', 'reversed')
            and le.reconciliation_id is null
            and le.reconciliation_draft_id is null
            and (le.tx_debit <> 0 or le.tx_credit <> 0)
            for update of le;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);
        command.Parameters.AddWithValue("statement_date", statementDate);
        command.Parameters.AddWithValue("ledger_entry_ids", ledgerEntryIds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapLedgerEntry(reader));
        }

        return items;
    }

    private static async Task InsertReconciliationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reconciliationId,
        CompanyId companyId,
        UserId completedByUserId,
        BankReconciliationCompleteInput input,
        BankReconciliationCalculation calculation,
        DateTimeOffset completedAt,
        int lineCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into bank_reconciliations (
              id,
              company_id,
              bank_account_id,
              statement_date,
              opening_balance,
              ending_balance,
              cleared_increase,
              cleared_decrease,
              calculated_ending_balance,
              difference,
              status,
              line_count,
              notes,
              completed_by_user_id,
              completed_at,
              created_at
            )
            values (
              @id,
              @company_id,
              @bank_account_id,
              @statement_date,
              @opening_balance,
              @ending_balance,
              @cleared_increase,
              @cleared_decrease,
              @calculated_ending_balance,
              @difference,
              'completed',
              @line_count,
              @notes,
              @completed_by_user_id,
              @completed_at,
              @completed_at
            );
            """;
        command.Parameters.AddWithValue("id", reconciliationId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", input.BankAccountId);
        command.Parameters.AddWithValue("statement_date", input.StatementDate);
        command.Parameters.AddWithValue("opening_balance", calculation.OpeningBalance);
        command.Parameters.AddWithValue("ending_balance", calculation.StatementEndingBalance);
        command.Parameters.AddWithValue("cleared_increase", calculation.ClearedIncrease);
        command.Parameters.AddWithValue("cleared_decrease", calculation.ClearedDecrease);
        command.Parameters.AddWithValue("calculated_ending_balance", calculation.CalculatedEndingBalance);
        command.Parameters.AddWithValue("difference", calculation.Difference);
        command.Parameters.AddWithValue("line_count", lineCount);
        command.Parameters.AddWithValue("notes", (object?)input.Notes?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("completed_by_user_id", completedByUserId.Value);
        command.Parameters.AddWithValue("completed_at", completedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // R-5 (P1): bulk-insert reconciliation_lines via unnest() over per-
    // column arrays. The original per-row loop issued one round-trip per
    // ledger entry while holding the SERIALIZABLE tx + JE-immutability
    // trigger context — a 200-line statement = 200 sequential network
    // hops. This single statement is N× faster (one round-trip, one
    // planner pass, no per-row trigger context churn) and the unnest
    // pattern is the idiomatic Postgres bulk-insert when you already
    // have the rows materialized in app memory.
    private static async Task InsertReconciliationLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reconciliationId,
        CompanyId companyId,
        IReadOnlyList<BankReconciliationLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var ledgerEntryIds = new Guid[entries.Count];
        var journalEntryIds = new Guid[entries.Count];
        var journalEntryLineIds = new Guid[entries.Count];
        var postingDates = new DateOnly[entries.Count];
        var transactionCurrencyCodes = new string[entries.Count];
        var txDebits = new decimal[entries.Count];
        var txCredits = new decimal[entries.Count];
        var debits = new decimal[entries.Count];
        var credits = new decimal[entries.Count];
        var signedAmountBases = new decimal[entries.Count];
        var signedAmountTransactions = new decimal[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            ledgerEntryIds[i] = e.LedgerEntryId;
            journalEntryIds[i] = e.JournalEntryId;
            journalEntryLineIds[i] = e.JournalEntryLineId;
            postingDates[i] = e.PostingDate;
            transactionCurrencyCodes[i] = e.TransactionCurrencyCode;
            txDebits[i] = e.TxDebit;
            txCredits[i] = e.TxCredit;
            debits[i] = e.Debit;
            credits[i] = e.Credit;
            signedAmountBases[i] = e.SignedAmountBase;
            signedAmountTransactions[i] = e.SignedAmountTransaction;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            insert into bank_reconciliation_lines (
              reconciliation_id,
              company_id,
              ledger_entry_id,
              journal_entry_id,
              journal_entry_line_id,
              posting_date,
              transaction_currency_code,
              tx_debit,
              tx_credit,
              debit,
              credit,
              signed_amount_base,
              signed_amount_transaction
            )
            select
              @reconciliation_id,
              @company_id,
              t.ledger_entry_id,
              t.journal_entry_id,
              t.journal_entry_line_id,
              t.posting_date,
              t.transaction_currency_code,
              t.tx_debit,
              t.tx_credit,
              t.debit,
              t.credit,
              t.signed_amount_base,
              t.signed_amount_transaction
            from unnest(
              @ledger_entry_ids,
              @journal_entry_ids,
              @journal_entry_line_ids,
              @posting_dates,
              @transaction_currency_codes,
              @tx_debits,
              @tx_credits,
              @debits,
              @credits,
              @signed_amount_bases,
              @signed_amount_transactions
            ) as t (
              ledger_entry_id,
              journal_entry_id,
              journal_entry_line_id,
              posting_date,
              transaction_currency_code,
              tx_debit,
              tx_credit,
              debit,
              credit,
              signed_amount_base,
              signed_amount_transaction
            );
            """;
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("ledger_entry_ids", ledgerEntryIds);
        command.Parameters.AddWithValue("journal_entry_ids", journalEntryIds);
        command.Parameters.AddWithValue("journal_entry_line_ids", journalEntryLineIds);
        command.Parameters.AddWithValue("posting_dates", postingDates);
        command.Parameters.AddWithValue("transaction_currency_codes", transactionCurrencyCodes);
        command.Parameters.AddWithValue("tx_debits", txDebits);
        command.Parameters.AddWithValue("tx_credits", txCredits);
        command.Parameters.AddWithValue("debits", debits);
        command.Parameters.AddWithValue("credits", credits);
        command.Parameters.AddWithValue("signed_amount_bases", signedAmountBases);
        command.Parameters.AddWithValue("signed_amount_transactions", signedAmountTransactions);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> LoadMoneyDecimalsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select money_decimals from companies where id = @company_id;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        var decimals = raw is null or DBNull ? 2 : Convert.ToInt32(raw);
        return decimals is 2 or 3 ? decimals : 2;
    }

    private static string LedgerEntrySelectSql(string predicate) =>
        $$"""
        select
          le.id as ledger_entry_id,
          le.journal_entry_id,
          le.journal_entry_line_id,
          le.posting_date,
          le.account_id,
          a.code as account_code,
          a.name as account_name,
          je.display_number,
          je.source_type,
          je.source_id,
          le.transaction_currency_code,
          le.tx_debit,
          le.tx_credit,
          le.debit,
          le.credit,
          case
            when a.root_type in ('asset', 'expense', 'cost_of_sales') then le.debit - le.credit
            else le.credit - le.debit
          end as signed_amount_base,
          case
            when a.root_type in ('asset', 'expense', 'cost_of_sales') then le.tx_debit - le.tx_credit
            else le.tx_credit - le.tx_debit
          end as signed_amount_transaction,
          coalesce(jel.description, '') as description,
          le.reconciliation_draft_id
        from ledger_entries le
        inner join journal_entries je
          on je.company_id = le.company_id
         and je.id = le.journal_entry_id
        inner join journal_entry_lines jel
          on jel.company_id = le.company_id
         and jel.id = le.journal_entry_line_id
        inner join accounts a
          on a.company_id = le.company_id
         and a.id = le.account_id
        where {{predicate}}
        """;

    private static BankReconciliationLedgerEntry MapLedgerEntry(NpgsqlDataReader reader) => new(
        reader.GetGuid(reader.GetOrdinal("ledger_entry_id")),
        reader.GetGuid(reader.GetOrdinal("journal_entry_id")),
        reader.GetGuid(reader.GetOrdinal("journal_entry_line_id")),
        reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
        reader.GetGuid(reader.GetOrdinal("account_id")),
        reader.GetString(reader.GetOrdinal("account_code")),
        reader.GetString(reader.GetOrdinal("account_name")),
        reader.GetString(reader.GetOrdinal("display_number")),
        reader.GetString(reader.GetOrdinal("source_type")),
        reader.GetGuid(reader.GetOrdinal("source_id")),
        reader.GetString(reader.GetOrdinal("transaction_currency_code")),
        reader.GetDecimal(reader.GetOrdinal("tx_debit")),
        reader.GetDecimal(reader.GetOrdinal("tx_credit")),
        reader.GetDecimal(reader.GetOrdinal("debit")),
        reader.GetDecimal(reader.GetOrdinal("credit")),
        reader.GetDecimal(reader.GetOrdinal("signed_amount_base")),
        reader.GetDecimal(reader.GetOrdinal("signed_amount_transaction")),
        reader.GetString(reader.GetOrdinal("description")));

    // =====================================================================
    // R-1: draft lifecycle. See BANKING_RECONCILE_PLAN.md sections 7 / 10.
    //
    // Backwards-compatibility note: the existing ListUnreconciledLedgerEntriesAsync
    // and CompleteAsync paths above continue to work against the new schema
    // (the column-based reconciliation_id check is equivalent to the existing
    // NOT EXISTS bank_reconciliation_lines join). R-3 replaces those paths
    // with draft-driven equivalents; for now they're left in place so the
    // existing /reconciliation page doesn't break.
    // =====================================================================

    public async Task<BankReconciliationDraft> OpenDraftAsync(
        CompanyId companyId,
        UserId createdByUserId,
        BankReconciliationDraftOpenInput input,
        CancellationToken cancellationToken)
    {
        if (input.BankAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Statement account is required.");
        }
        if (input.StatementDate == default)
        {
            throw new InvalidOperationException("Statement date is required.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await EnsureReconcilableAccountAsync(connection, transaction, companyId, input.BankAccountId, cancellationToken);

            // R-4 carry-forward audit. Look up the most recent
            // completed reconciliation; if its ending_balance doesn't
            // match the operator's submitted opening_balance (beyond
            // rounding tolerance), prepend an audit note so the
            // override is visible on the completed reconciliation
            // forever. Operator is still allowed to override — banks
            // sometimes start a fresh series after an account
            // migration, and the operator alone owns that judgment.
            var lastCompleted = await GetLastCompletedCoreAsync(
                connection, transaction, companyId, input.BankAccountId, cancellationToken);
            var notesBuilder = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
            if (lastCompleted is not null &&
                Math.Abs(lastCompleted.EndingBalance - input.OpeningBalance) >= BankReconciliationPolicy.ZeroTolerance)
            {
                var auditLine =
                    $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z] Operator overrode auto-derived beginning balance " +
                    $"from {lastCompleted.EndingBalance:N2} (prior reconciliation {lastCompleted.ReconciliationId:D}, " +
                    $"statement {lastCompleted.StatementDate:yyyy-MM-dd}) to {input.OpeningBalance:N2}.";
                notesBuilder = string.IsNullOrWhiteSpace(notesBuilder)
                    ? auditLine
                    : auditLine + Environment.NewLine + notesBuilder;
            }

            var draftId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var difference = input.EndingBalance - input.OpeningBalance;

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    insert into bank_reconciliations (
                      id, company_id, bank_account_id, statement_date,
                      opening_balance, ending_balance,
                      cleared_increase, cleared_decrease,
                      calculated_ending_balance, difference,
                      status, line_count, notes,
                      created_by_user_id, last_modified_at,
                      completed_by_user_id, completed_at, created_at
                    )
                    values (
                      @id, @company_id, @bank_account_id, @statement_date,
                      @opening_balance, @ending_balance,
                      0, 0,
                      @opening_balance, @difference,
                      'in_progress', 0, @notes,
                      @created_by_user_id, @now,
                      null, null, @now
                    );
                    """;
                command.Parameters.AddWithValue("id", draftId);
                command.Parameters.AddWithValue("company_id", companyId.Value);
                command.Parameters.AddWithValue("bank_account_id", input.BankAccountId);
                command.Parameters.AddWithValue("statement_date", input.StatementDate);
                command.Parameters.AddWithValue("opening_balance", input.OpeningBalance);
                command.Parameters.AddWithValue("ending_balance", input.EndingBalance);
                command.Parameters.AddWithValue("difference", difference);
                command.Parameters.AddWithValue("notes", (object?)notesBuilder ?? DBNull.Value);
                command.Parameters.AddWithValue("created_by_user_id", createdByUserId.Value);
                command.Parameters.AddWithValue("now", now);
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState == "23505" &&
                    ex.ConstraintName == "ux_bank_reconciliations_in_progress_per_account")
                {
                    throw new InvalidOperationException(
                        "draft_already_open_for_account: another reconciliation draft is already open for this account. Resume or close it first.");
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return new BankReconciliationDraft(
                draftId,
                input.BankAccountId,
                input.StatementDate,
                input.OpeningBalance,
                input.EndingBalance,
                0m,
                0m,
                input.OpeningBalance,
                difference,
                0,
                notesBuilder,
                createdByUserId,
                now,
                now);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<BankReconciliationLastCompleted?> GetLastCompletedAsync(
        CompanyId companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await GetLastCompletedCoreAsync(connection, null, companyId, bankAccountId, cancellationToken);
    }

    private static async Task<BankReconciliationLastCompleted?> GetLastCompletedCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, statement_date, ending_balance, completed_at
            from bank_reconciliations
            where company_id = @company_id
              and bank_account_id = @bank_account_id
              and status = 'completed'
            order by statement_date desc, completed_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new BankReconciliationLastCompleted(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("statement_date")),
            reader.GetDecimal(reader.GetOrdinal("ending_balance")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("completed_at")));
    }

    public async Task<BankReconciliationReport?> LoadReconciliationReportAsync(
        CompanyId companyId,
        Guid reconciliationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // Header + account display.
        BankReconciliationReport? report;
        await using (var header = connection.CreateCommand())
        {
            header.CommandText =
                """
                select br.id, br.bank_account_id, a.code, a.name,
                       br.statement_date, br.status,
                       br.opening_balance, br.ending_balance,
                       br.cleared_increase, br.cleared_decrease,
                       br.calculated_ending_balance, br.difference,
                       br.line_count, br.notes,
                       br.created_by_user_id, br.created_at,
                       br.completed_by_user_id, br.completed_at,
                       br.abandoned_by_user_id, br.abandoned_at
                from bank_reconciliations br
                inner join accounts a
                  on a.company_id = br.company_id and a.id = br.bank_account_id
                where br.company_id = @company_id
                  and br.id = @id
                  and br.status in ('completed', 'abandoned');
                """;
            header.Parameters.AddWithValue("company_id", companyId.Value);
            header.Parameters.AddWithValue("id", reconciliationId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            UserId? created = reader.IsDBNull(reader.GetOrdinal("created_by_user_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id")));
            UserId? completed = reader.IsDBNull(reader.GetOrdinal("completed_by_user_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("completed_by_user_id")));
            UserId? abandoned = reader.IsDBNull(reader.GetOrdinal("abandoned_by_user_id"))
                ? null
                : UserId.Parse(reader.GetString(reader.GetOrdinal("abandoned_by_user_id")));
            DateTimeOffset? completedAt = reader.IsDBNull(reader.GetOrdinal("completed_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("completed_at"));
            DateTimeOffset? abandonedAt = reader.IsDBNull(reader.GetOrdinal("abandoned_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("abandoned_at"));
            string? notes = reader.IsDBNull(reader.GetOrdinal("notes"))
                ? null
                : reader.GetString(reader.GetOrdinal("notes"));

            report = new BankReconciliationReport(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("bank_account_id")),
                reader.GetString(reader.GetOrdinal("code")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("statement_date")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetDecimal(reader.GetOrdinal("opening_balance")),
                reader.GetDecimal(reader.GetOrdinal("ending_balance")),
                reader.GetDecimal(reader.GetOrdinal("cleared_increase")),
                reader.GetDecimal(reader.GetOrdinal("cleared_decrease")),
                reader.GetDecimal(reader.GetOrdinal("calculated_ending_balance")),
                reader.GetDecimal(reader.GetOrdinal("difference")),
                reader.GetInt32(reader.GetOrdinal("line_count")),
                notes,
                created,
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                completed,
                completedAt,
                abandoned,
                abandonedAt,
                Array.Empty<BankReconciliationReportLine>());
        }

        // Lines snapshot. Empty list for abandoned reconciliations
        // (Undo deletes them) — that's intentional, the header is
        // still preserved for audit but the financial detail is in
        // the new reconciliation that re-cleared those entries.
        var lines = new List<BankReconciliationReportLine>();
        await using (var lineCmd = connection.CreateCommand())
        {
            lineCmd.CommandText =
                """
                select brl.ledger_entry_id, brl.journal_entry_id, brl.journal_entry_line_id,
                       brl.posting_date, je.display_number,
                       a.code as account_code, a.name as account_name,
                       coalesce(jel.description, '') as description,
                       brl.transaction_currency_code,
                       brl.tx_debit, brl.tx_credit, brl.debit, brl.credit,
                       brl.signed_amount_base, brl.signed_amount_transaction
                from bank_reconciliation_lines brl
                inner join journal_entries je
                  on je.company_id = brl.company_id and je.id = brl.journal_entry_id
                inner join journal_entry_lines jel
                  on jel.company_id = brl.company_id and jel.id = brl.journal_entry_line_id
                inner join ledger_entries le
                  on le.company_id = brl.company_id and le.id = brl.ledger_entry_id
                inner join accounts a
                  on a.company_id = brl.company_id and a.id = le.account_id
                where brl.company_id = @company_id
                  and brl.reconciliation_id = @id
                order by brl.posting_date asc, brl.id asc;
                """;
            lineCmd.Parameters.AddWithValue("company_id", companyId.Value);
            lineCmd.Parameters.AddWithValue("id", reconciliationId);
            await using var reader = await lineCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new BankReconciliationReportLine(
                    reader.GetGuid(reader.GetOrdinal("ledger_entry_id")),
                    reader.GetGuid(reader.GetOrdinal("journal_entry_id")),
                    reader.GetGuid(reader.GetOrdinal("journal_entry_line_id")),
                    reader.GetFieldValue<DateOnly>(reader.GetOrdinal("posting_date")),
                    reader.GetString(reader.GetOrdinal("display_number")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                    reader.GetDecimal(reader.GetOrdinal("tx_debit")),
                    reader.GetDecimal(reader.GetOrdinal("tx_credit")),
                    reader.GetDecimal(reader.GetOrdinal("debit")),
                    reader.GetDecimal(reader.GetOrdinal("credit")),
                    reader.GetDecimal(reader.GetOrdinal("signed_amount_base")),
                    reader.GetDecimal(reader.GetOrdinal("signed_amount_transaction"))));
            }
        }

        return report with { Lines = lines };
    }

    public async Task<BankReconciliationDraft?> LoadDraftAsync(
        CompanyId companyId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await LoadDraftCoreAsync(connection, null, companyId, draftId, cancellationToken);
    }

    public async Task<BankReconciliationDraft?> FindOpenDraftForAccountAsync(
        CompanyId companyId,
        Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id
            from bank_reconciliations
            where company_id = @company_id
              and bank_account_id = @bank_account_id
              and status = 'in_progress'
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        if (raw is not Guid draftId)
        {
            return null;
        }
        return await LoadDraftCoreAsync(connection, null, companyId, draftId, cancellationToken);
    }

    public async Task<IReadOnlyList<BankReconciliationDraftCandidate>> ListDraftCandidatesAsync(
        CompanyId companyId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var draft = await LoadDraftCoreAsync(connection, null, companyId, draftId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Reconciliation draft '{draftId:D}' was not found in the active company or is no longer in progress.");

        var items = new List<BankReconciliationDraftCandidate>();
        await using var command = connection.CreateCommand();
        // R-5 (P3): add a hard LIMIT so a pathologically stuck bank
        // account (one that has accumulated >2000 unreconciled entries
        // without ever being cleaned up) doesn't blow up the page load.
        // Typical statement sizes are 100-500 lines; 2000 leaves a
        // comfortable headroom. The partial index
        // ix_ledger_entries_company_account_unreconciled already prunes
        // to unreconciled rows for the account, so this is a defensive
        // cap rather than the primary performance gate.
        //
        // No posting_date lower bound: aged outstanding items (e.g. a
        // cheque issued 18 months ago that still hasn't cleared) are
        // legitimate and the operator must be able to see and clear
        // them. Filtering by date here would silently hide them.
        //
        // Status set includes 'voided'/'reversed', not just 'posted':
        // when a JE is voided/reversed the original keeps its bank-line
        // amounts and a separate posted compensation JE carries the
        // offset. Showing only 'posted' would surface the compensation
        // as an orphan with no partner to net against, leaving the
        // register impossible to balance. Including the voided/reversed
        // original lets the operator clear both legs together (they sum
        // to zero). A compensation of an already-reconciled item is
        // unaffected: the reconciled original is excluded by the
        // reconciliation_id IS NULL filter below.
        command.CommandText = LedgerEntrySelectSql(
            """
            le.company_id = @company_id
              and le.account_id = @bank_account_id
              and le.posting_date <= @statement_date
              and je.status in ('posted', 'voided', 'reversed')
              and le.reconciliation_id is null
              and (le.reconciliation_draft_id is null
                   or le.reconciliation_draft_id = @draft_id)
              and (le.tx_debit <> 0 or le.tx_credit <> 0)
              order by le.posting_date asc, le.created_at asc, le.id asc
              limit 2000
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", draft.BankAccountId);
        command.Parameters.AddWithValue("statement_date", draft.StatementDate);
        command.Parameters.AddWithValue("draft_id", draftId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var draftCol = -1;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (draftCol < 0)
            {
                draftCol = reader.GetOrdinal("reconciliation_draft_id");
            }
            var entry = MapLedgerEntry(reader);
            var cleared = !reader.IsDBNull(draftCol)
                && reader.GetGuid(draftCol) == draftId;
            items.Add(new BankReconciliationDraftCandidate(entry, cleared));
        }

        return items;
    }

    public async Task<BankReconciliationDraft> ToggleLineAsync(
        CompanyId companyId,
        Guid draftId,
        Guid ledgerEntryId,
        bool cleared,
        CancellationToken cancellationToken)
    {
        if (ledgerEntryId == Guid.Empty)
        {
            throw new InvalidOperationException("Ledger entry id is required.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // R-5 (P2): the pre-toggle path used to call LoadDraftCoreAsync
            // (full aggregate + accounts JOIN + cleared-line projection)
            // just to read bank_account_id and validate the draft is
            // still in_progress. On a 500-line statement that's the same
            // work as the post-toggle refresh, so every checkbox click
            // was paying 2× the aggregation cost. Replaced with a cheap
            // single-row read of (bank_account_id, status) — the same
            // validation, none of the aggregation.
            Guid bankAccountId;
            await using (var headerCmd = connection.CreateCommand())
            {
                headerCmd.Transaction = transaction;
                headerCmd.CommandText =
                    """
                    select bank_account_id, status
                    from bank_reconciliations
                    where company_id = @company_id
                      and id = @draft_id;
                    """;
                headerCmd.Parameters.AddWithValue("company_id", companyId.Value);
                headerCmd.Parameters.AddWithValue("draft_id", draftId);
                await using var reader = await headerCmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Reconciliation draft '{draftId:D}' was not found in the active company or is no longer in progress.");
                }
                bankAccountId = reader.GetGuid(reader.GetOrdinal("bank_account_id"));
                var status = reader.GetString(reader.GetOrdinal("status"));
                if (!string.Equals(status, BankReconciliationStatusTokens.InProgress, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Reconciliation draft '{draftId:D}' was not found in the active company or is no longer in progress.");
                }
            }

            int rows;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                if (cleared)
                {
                    // Mark cleared. Only flips entries that are
                    // currently unreconciled OR already in this same
                    // draft (idempotent re-check). Refuses entries in
                    // a different draft or in any completed
                    // reconciliation.
                    command.CommandText =
                        """
                        update ledger_entries
                           set reconciliation_draft_id = @draft_id
                         where company_id = @company_id
                           and id = @ledger_entry_id
                           and account_id = @bank_account_id
                           and reconciliation_id is null
                           and (reconciliation_draft_id is null
                                or reconciliation_draft_id = @draft_id);
                        """;
                }
                else
                {
                    // Uncheck: only clears the mark if this draft owns
                    // it. Won't touch an entry owned by another draft
                    // or already completed.
                    command.CommandText =
                        """
                        update ledger_entries
                           set reconciliation_draft_id = null
                         where company_id = @company_id
                           and id = @ledger_entry_id
                           and account_id = @bank_account_id
                           and reconciliation_draft_id = @draft_id;
                        """;
                }
                command.Parameters.AddWithValue("company_id", companyId.Value);
                command.Parameters.AddWithValue("ledger_entry_id", ledgerEntryId);
                command.Parameters.AddWithValue("bank_account_id", bankAccountId);
                command.Parameters.AddWithValue("draft_id", draftId);
                rows = await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (rows == 0)
            {
                throw new InvalidOperationException(
                    "ledger_entry_not_in_account_or_locked: the ledger entry was not found on this bank account, " +
                    "is already in another draft, or has been completed in a prior reconciliation.");
            }

            await TouchDraftAsync(connection, transaction, draftId, cancellationToken);
            var refreshed = await LoadDraftCoreAsync(connection, transaction, companyId, draftId, cancellationToken)
                ?? throw new InvalidOperationException("Draft disappeared during toggle.");
            await transaction.CommitAsync(cancellationToken);
            return refreshed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<BankReconciliationDraft> PatchStatementInfoAsync(
        CompanyId companyId,
        Guid draftId,
        BankReconciliationDraftPatchInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var draft = await LoadDraftCoreAsync(connection, transaction, companyId, draftId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Reconciliation draft '{draftId:D}' was not found in the active company or is no longer in progress.");

            var openingBalance = input.OpeningBalance ?? draft.OpeningBalance;
            var endingBalance = input.EndingBalance ?? draft.EndingBalance;
            var statementDate = input.StatementDate ?? draft.StatementDate;
            var notes = input.Notes is null ? draft.Notes : input.Notes.Trim();

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    update bank_reconciliations
                       set opening_balance = @opening_balance,
                           ending_balance = @ending_balance,
                           statement_date = @statement_date,
                           notes = @notes,
                           last_modified_at = now()
                     where company_id = @company_id
                       and id = @id
                       and status = 'in_progress';
                    """;
                command.Parameters.AddWithValue("opening_balance", openingBalance);
                command.Parameters.AddWithValue("ending_balance", endingBalance);
                command.Parameters.AddWithValue("statement_date", statementDate);
                command.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
                command.Parameters.AddWithValue("company_id", companyId.Value);
                command.Parameters.AddWithValue("id", draftId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var refreshed = await LoadDraftCoreAsync(connection, transaction, companyId, draftId, cancellationToken)
                ?? throw new InvalidOperationException("Draft disappeared during patch.");
            await transaction.CommitAsync(cancellationToken);
            return refreshed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task AbandonDraftAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            // Lock the header row first; ensures we see a consistent
            // status and prevents a race where two callers both try
            // to abandon.
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.Transaction = transaction;
                lockCmd.CommandText =
                    """
                    select status
                    from bank_reconciliations
                    where company_id = @company_id and id = @id
                    for update;
                    """;
                lockCmd.Parameters.AddWithValue("company_id", companyId.Value);
                lockCmd.Parameters.AddWithValue("id", draftId);
                var status = await lockCmd.ExecuteScalarAsync(cancellationToken) as string;
                if (status is null)
                {
                    throw new InvalidOperationException(
                        $"Reconciliation draft '{draftId:D}' was not found in the active company.");
                }
                if (!string.Equals(status, BankReconciliationStatusTokens.InProgress, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"not_in_progress: reconciliation '{draftId:D}' has status '{status}' and cannot be abandoned.");
                }
            }

            await using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.Transaction = transaction;
                clearCmd.CommandText =
                    """
                    update ledger_entries
                       set reconciliation_draft_id = null
                     where company_id = @company_id
                       and reconciliation_draft_id = @draft_id;
                    """;
                clearCmd.Parameters.AddWithValue("company_id", companyId.Value);
                clearCmd.Parameters.AddWithValue("draft_id", draftId);
                await clearCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var headerCmd = connection.CreateCommand())
            {
                headerCmd.Transaction = transaction;
                headerCmd.CommandText =
                    """
                    update bank_reconciliations
                       set status = 'abandoned',
                           abandoned_at = now(),
                           abandoned_by_user_id = @actor,
                           last_modified_at = now()
                     where company_id = @company_id
                       and id = @id;
                    """;
                headerCmd.Parameters.AddWithValue("actor", actorUserId.Value);
                headerCmd.Parameters.AddWithValue("company_id", companyId.Value);
                headerCmd.Parameters.AddWithValue("id", draftId);
                await headerCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<BankReconciliationSummary> CompleteDraftAsync(
        CompanyId companyId,
        UserId completedByUserId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            // Lock the header.
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.Transaction = transaction;
                lockCmd.CommandText =
                    """
                    select status
                    from bank_reconciliations
                    where company_id = @company_id and id = @id
                    for update;
                    """;
                lockCmd.Parameters.AddWithValue("company_id", companyId.Value);
                lockCmd.Parameters.AddWithValue("id", draftId);
                var status = await lockCmd.ExecuteScalarAsync(cancellationToken) as string;
                if (status is null)
                {
                    throw new InvalidOperationException(
                        $"Reconciliation draft '{draftId:D}' was not found in the active company.");
                }
                if (!string.Equals(status, BankReconciliationStatusTokens.InProgress, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"not_in_progress: reconciliation '{draftId:D}' has status '{status}' and cannot be completed.");
                }
            }

            // Load draft + cleared entries with FOR UPDATE on the
            // ledger rows so the JE-locking trigger sees a stable set.
            var draft = await LoadDraftCoreAsync(connection, transaction, companyId, draftId, cancellationToken)
                ?? throw new InvalidOperationException("Draft disappeared between lock and load.");

            var moneyDecimals = await LoadMoneyDecimalsAsync(connection, transaction, companyId, cancellationToken);
            if (!BankReconciliationPolicy.IsZeroDifference(draft.Difference, moneyDecimals))
            {
                throw new InvalidOperationException(
                    $"difference_nonzero: current difference is {draft.Difference:N2}. Reconciliation cannot complete until the difference is within {BankReconciliationPolicy.ToleranceFor(moneyDecimals)}.");
            }

            var lockedEntries = await LoadDraftLedgerEntriesForUpdateAsync(
                connection, transaction, companyId, draftId, cancellationToken);
            if (lockedEntries.Count != draft.ClearedLineCount)
            {
                throw new InvalidOperationException(
                    "Draft line count drifted during commit; retry the completion.");
            }

            var completedAt = DateTimeOffset.UtcNow;

            // Flip ledger_entries from draft to completed in one shot.
            await using (var flip = connection.CreateCommand())
            {
                flip.Transaction = transaction;
                flip.CommandText =
                    """
                    update ledger_entries
                       set reconciliation_id = @id,
                           reconciliation_draft_id = null
                     where company_id = @company_id
                       and reconciliation_draft_id = @id;
                    """;
                flip.Parameters.AddWithValue("id", draftId);
                flip.Parameters.AddWithValue("company_id", companyId.Value);
                await flip.ExecuteNonQueryAsync(cancellationToken);
            }

            // Promote the header to completed and stamp the totals.
            await using (var header = connection.CreateCommand())
            {
                header.Transaction = transaction;
                header.CommandText =
                    """
                    update bank_reconciliations
                       set status = 'completed',
                           cleared_increase = @cleared_increase,
                           cleared_decrease = @cleared_decrease,
                           calculated_ending_balance = @calculated_ending_balance,
                           difference = @difference,
                           line_count = @line_count,
                           completed_by_user_id = @completed_by_user_id,
                           completed_at = @completed_at,
                           last_modified_at = @completed_at
                     where company_id = @company_id
                       and id = @id;
                    """;
                header.Parameters.AddWithValue("cleared_increase", draft.ClearedIncrease);
                header.Parameters.AddWithValue("cleared_decrease", draft.ClearedDecrease);
                header.Parameters.AddWithValue("calculated_ending_balance", draft.CalculatedEndingBalance);
                header.Parameters.AddWithValue("difference", draft.Difference);
                header.Parameters.AddWithValue("line_count", draft.ClearedLineCount);
                header.Parameters.AddWithValue("completed_by_user_id", completedByUserId.Value);
                header.Parameters.AddWithValue("completed_at", completedAt);
                header.Parameters.AddWithValue("company_id", companyId.Value);
                header.Parameters.AddWithValue("id", draftId);
                await header.ExecuteNonQueryAsync(cancellationToken);
            }

            // Snapshot the cleared entries into the lines table (audit
            // defensive copy). R-5 (P1): bulk-inserted via unnest in a
            // single round-trip — see InsertReconciliationLinesAsync.
            await InsertReconciliationLinesAsync(
                connection,
                transaction,
                draftId,
                companyId,
                lockedEntries,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new BankReconciliationSummary(
                draftId,
                draft.BankAccountId,
                draft.StatementDate,
                draft.OpeningBalance,
                draft.EndingBalance,
                draft.ClearedIncrease,
                draft.ClearedDecrease,
                draft.CalculatedEndingBalance,
                draft.Difference,
                draft.ClearedLineCount,
                completedByUserId,
                completedAt);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UndoCompletedAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid reconciliationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            // Lock the target reconciliation row.
            Guid bankAccountId;
            DateOnly statementDate;
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.Transaction = transaction;
                lockCmd.CommandText =
                    """
                    select bank_account_id, statement_date, status
                    from bank_reconciliations
                    where company_id = @company_id and id = @id
                    for update;
                    """;
                lockCmd.Parameters.AddWithValue("company_id", companyId.Value);
                lockCmd.Parameters.AddWithValue("id", reconciliationId);
                await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Reconciliation '{reconciliationId:D}' was not found in the active company.");
                }
                var status = reader.GetString(reader.GetOrdinal("status"));
                if (!string.Equals(status, BankReconciliationStatusTokens.Completed, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"not_completed: reconciliation '{reconciliationId:D}' has status '{status}' and cannot be undone.");
                }
                bankAccountId = reader.GetGuid(reader.GetOrdinal("bank_account_id"));
                statementDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("statement_date"));
            }

            // LIFO check: any later completed reconciliation for the
            // same account?
            await using (var lifo = connection.CreateCommand())
            {
                lifo.Transaction = transaction;
                lifo.CommandText =
                    """
                    select id, statement_date
                    from bank_reconciliations
                    where company_id = @company_id
                      and bank_account_id = @bank_account_id
                      and status = 'completed'
                      and (statement_date > @statement_date
                           or (statement_date = @statement_date
                               and completed_at > (
                                 select completed_at
                                 from bank_reconciliations
                                 where company_id = @company_id and id = @id)))
                    order by statement_date desc, completed_at desc
                    limit 1;
                    """;
                lifo.Parameters.AddWithValue("company_id", companyId.Value);
                lifo.Parameters.AddWithValue("bank_account_id", bankAccountId);
                lifo.Parameters.AddWithValue("statement_date", statementDate);
                lifo.Parameters.AddWithValue("id", reconciliationId);
                await using var reader = await lifo.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var laterId = reader.GetGuid(reader.GetOrdinal("id"));
                    var laterDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("statement_date"));
                    throw new InvalidOperationException(
                        $"reconciliation_undo_not_latest: a later reconciliation exists ({laterId:D}, " +
                        $"{laterDate:yyyy-MM-dd}). Undo it first.");
                }
            }

            // Clear ledger pointers.
            await using (var clearLedger = connection.CreateCommand())
            {
                clearLedger.Transaction = transaction;
                clearLedger.CommandText =
                    """
                    update ledger_entries
                       set reconciliation_id = null
                     where company_id = @company_id
                       and reconciliation_id = @id;
                    """;
                clearLedger.Parameters.AddWithValue("company_id", companyId.Value);
                clearLedger.Parameters.AddWithValue("id", reconciliationId);
                await clearLedger.ExecuteNonQueryAsync(cancellationToken);
            }

            // Delete snapshot rows.
            await using (var deleteLines = connection.CreateCommand())
            {
                deleteLines.Transaction = transaction;
                deleteLines.CommandText =
                    """
                    delete from bank_reconciliation_lines
                     where company_id = @company_id
                       and reconciliation_id = @id;
                    """;
                deleteLines.Parameters.AddWithValue("company_id", companyId.Value);
                deleteLines.Parameters.AddWithValue("id", reconciliationId);
                await deleteLines.ExecuteNonQueryAsync(cancellationToken);
            }

            // Mark header abandoned (retained for audit).
            await using (var abandonHeader = connection.CreateCommand())
            {
                abandonHeader.Transaction = transaction;
                abandonHeader.CommandText =
                    """
                    update bank_reconciliations
                       set status = 'abandoned',
                           abandoned_at = now(),
                           abandoned_by_user_id = @actor,
                           last_modified_at = now()
                     where company_id = @company_id
                       and id = @id;
                    """;
                abandonHeader.Parameters.AddWithValue("actor", actorUserId.Value);
                abandonHeader.Parameters.AddWithValue("company_id", companyId.Value);
                abandonHeader.Parameters.AddWithValue("id", reconciliationId);
                await abandonHeader.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // R-1 internal helpers
    // ---------------------------------------------------------------------

    private static async Task<BankReconciliationDraft?> LoadDraftCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CompanyId companyId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        // Header + live totals computed from ledger_entries via a
        // LEFT JOIN so an empty draft (0 cleared lines) still returns
        // a row.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select br.id,
                   br.bank_account_id,
                   br.statement_date,
                   br.opening_balance,
                   br.ending_balance,
                   br.notes,
                   br.created_by_user_id,
                   br.created_at,
                   br.last_modified_at,
                   coalesce(t.cleared_increase, 0) as cleared_increase,
                   coalesce(t.cleared_decrease, 0) as cleared_decrease,
                   coalesce(t.line_count, 0) as line_count
              from bank_reconciliations br
              left join lateral (
                select
                  sum(case
                        when (a.root_type in ('asset', 'expense', 'cost_of_sales') and (le.debit - le.credit) > 0)
                          then (le.debit - le.credit)
                        when (a.root_type not in ('asset', 'expense', 'cost_of_sales') and (le.credit - le.debit) > 0)
                          then (le.credit - le.debit)
                        else 0
                      end) as cleared_increase,
                  sum(case
                        when (a.root_type in ('asset', 'expense', 'cost_of_sales') and (le.debit - le.credit) < 0)
                          then abs(le.debit - le.credit)
                        when (a.root_type not in ('asset', 'expense', 'cost_of_sales') and (le.credit - le.debit) < 0)
                          then abs(le.credit - le.debit)
                        else 0
                      end) as cleared_decrease,
                  count(*) as line_count
                from ledger_entries le
                inner join accounts a on a.company_id = le.company_id and a.id = le.account_id
                where le.company_id = br.company_id
                  and le.reconciliation_draft_id = br.id
              ) t on true
             where br.company_id = @company_id
               and br.id = @id
               and br.status = 'in_progress';
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", draftId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var bankAccountId = reader.GetGuid(reader.GetOrdinal("bank_account_id"));
        var statementDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("statement_date"));
        var openingBalance = reader.GetDecimal(reader.GetOrdinal("opening_balance"));
        var endingBalance = reader.GetDecimal(reader.GetOrdinal("ending_balance"));
        var notesOrd = reader.GetOrdinal("notes");
        var notes = reader.IsDBNull(notesOrd) ? null : reader.GetString(notesOrd);
        var createdByUserId = UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id")));
        var createdAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"));
        var lastModifiedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_modified_at"));
        var clearedIncrease = reader.GetDecimal(reader.GetOrdinal("cleared_increase"));
        var clearedDecrease = reader.GetDecimal(reader.GetOrdinal("cleared_decrease"));
        var lineCount = reader.GetInt64(reader.GetOrdinal("line_count"));
        var calculatedEnding = openingBalance + clearedIncrease - clearedDecrease;
        var difference = endingBalance - calculatedEnding;

        return new BankReconciliationDraft(
            draftId,
            bankAccountId,
            statementDate,
            openingBalance,
            endingBalance,
            clearedIncrease,
            clearedDecrease,
            calculatedEnding,
            difference,
            (int)lineCount,
            notes,
            createdByUserId,
            createdAt,
            lastModifiedAt);
    }

    private static async Task TouchDraftAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update bank_reconciliations
               set last_modified_at = now()
             where id = @id
               and status = 'in_progress';
            """;
        command.Parameters.AddWithValue("id", draftId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BankRegisterEntry>> ListBankRegisterAsync(
        CompanyId companyId,
        Guid bankAccountId,
        int take,
        CancellationToken cancellationToken)
    {
        if (bankAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Bank register requires an account.");
        }
        if (take <= 0 || take > 500)
        {
            take = Math.Clamp(take, 1, 200);
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureReconcilableAccountAsync(connection, null, companyId, bankAccountId, cancellationToken);

        var items = new List<BankRegisterEntry>();
        await using var command = connection.CreateCommand();
        // Mirror of LedgerEntrySelectSql but with the LEFT JOIN to
        // bank_reconciliations so we can return statement_date when
        // the row is cleared. Ordered posting_date DESC so the most-
        // recent activity shows first (register view, not statement
        // view).
        command.CommandText =
            """
            select
              le.id as ledger_entry_id,
              le.journal_entry_id,
              le.journal_entry_line_id,
              le.posting_date,
              le.account_id,
              a.code as account_code,
              a.name as account_name,
              je.display_number,
              je.source_type,
              je.source_id,
              le.transaction_currency_code,
              le.tx_debit,
              le.tx_credit,
              le.debit,
              le.credit,
              case
                when a.root_type in ('asset', 'expense', 'cost_of_sales') then le.debit - le.credit
                else le.credit - le.debit
              end as signed_amount_base,
              case
                when a.root_type in ('asset', 'expense', 'cost_of_sales') then le.tx_debit - le.tx_credit
                else le.tx_credit - le.tx_debit
              end as signed_amount_transaction,
              coalesce(jel.description, '') as description,
              le.reconciliation_draft_id,
              le.reconciliation_id,
              br.statement_date as cleared_on_statement_date
            from ledger_entries le
            inner join journal_entries je
              on je.company_id = le.company_id
             and je.id = le.journal_entry_id
            inner join journal_entry_lines jel
              on jel.company_id = le.company_id
             and jel.id = le.journal_entry_line_id
            inner join accounts a
              on a.company_id = le.company_id
             and a.id = le.account_id
            left join bank_reconciliations br
              on br.company_id = le.company_id
             and br.id = le.reconciliation_id
            where le.company_id = @company_id
              and le.account_id = @bank_account_id
              and je.status = 'posted'
            order by le.posting_date desc, le.created_at desc, le.id desc
            limit @take;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("bank_account_id", bankAccountId);
        command.Parameters.AddWithValue("take", take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var reconciliationIdCol = -1;
        var reconciliationDraftIdCol = -1;
        var clearedStatementDateCol = -1;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reconciliationIdCol < 0)
            {
                reconciliationIdCol = reader.GetOrdinal("reconciliation_id");
                reconciliationDraftIdCol = reader.GetOrdinal("reconciliation_draft_id");
                clearedStatementDateCol = reader.GetOrdinal("cleared_on_statement_date");
            }
            var entry = MapLedgerEntry(reader);
            var reconciliationId = reader.IsDBNull(reconciliationIdCol)
                ? (Guid?)null
                : reader.GetGuid(reconciliationIdCol);
            var isInDraft = !reader.IsDBNull(reconciliationDraftIdCol);
            var clearedOn = reader.IsDBNull(clearedStatementDateCol)
                ? (DateOnly?)null
                : reader.GetFieldValue<DateOnly>(clearedStatementDateCol);
            items.Add(new BankRegisterEntry(
                entry,
                reconciliationId.HasValue,
                isInDraft,
                reconciliationId,
                clearedOn));
        }

        return items;
    }

    private static async Task<IReadOnlyList<BankReconciliationLedgerEntry>> LoadDraftLedgerEntriesForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var items = new List<BankReconciliationLedgerEntry>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LedgerEntrySelectSql(
            // Mirror the candidate query's status set: a voided/reversed
            // original is offered as a reconcile candidate next to its
            // posted compensation so the pair nets to zero and clears
            // together. If this load stayed 'posted'-only it would drop the
            // ticked voided leg, the count would drift, and completion would
            // throw. The draft-id filter already scopes this to the operator's
            // selection.
            """
            le.company_id = @company_id
              and le.reconciliation_draft_id = @draft_id
              and je.status in ('posted', 'voided', 'reversed')
              order by le.posting_date asc, le.created_at asc, le.id asc
              for update of le
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("draft_id", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapLedgerEntry(reader));
        }
        return items;
    }
}
