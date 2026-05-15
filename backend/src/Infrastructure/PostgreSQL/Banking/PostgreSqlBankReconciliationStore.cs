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
            and je.status = 'posted'
            and not exists (
              select 1
              from bank_reconciliation_lines brl
              where brl.ledger_entry_id = le.id
            )
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

            var calculation = BankReconciliationPolicy.Calculate(
                input.OpeningBalance,
                input.EndingBalance,
                lockedEntries);
            if (!BankReconciliationPolicy.IsZeroDifference(calculation.Difference))
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

            foreach (var entry in lockedEntries)
            {
                await InsertReconciliationLineAsync(
                    connection,
                    transaction,
                    reconciliationId,
                    companyId,
                    entry,
                    cancellationToken);
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
            and je.status = 'posted'
            and not exists (
              select 1
              from bank_reconciliation_lines brl
              where brl.ledger_entry_id = le.id
            )
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

    private static async Task InsertReconciliationLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reconciliationId,
        CompanyId companyId,
        BankReconciliationLedgerEntry entry,
        CancellationToken cancellationToken)
    {
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
            values (
              @reconciliation_id,
              @company_id,
              @ledger_entry_id,
              @journal_entry_id,
              @journal_entry_line_id,
              @posting_date,
              @transaction_currency_code,
              @tx_debit,
              @tx_credit,
              @debit,
              @credit,
              @signed_amount_base,
              @signed_amount_transaction
            );
            """;
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("ledger_entry_id", entry.LedgerEntryId);
        command.Parameters.AddWithValue("journal_entry_id", entry.JournalEntryId);
        command.Parameters.AddWithValue("journal_entry_line_id", entry.JournalEntryLineId);
        command.Parameters.AddWithValue("posting_date", entry.PostingDate);
        command.Parameters.AddWithValue("transaction_currency_code", entry.TransactionCurrencyCode);
        command.Parameters.AddWithValue("tx_debit", entry.TxDebit);
        command.Parameters.AddWithValue("tx_credit", entry.TxCredit);
        command.Parameters.AddWithValue("debit", entry.Debit);
        command.Parameters.AddWithValue("credit", entry.Credit);
        command.Parameters.AddWithValue("signed_amount_base", entry.SignedAmountBase);
        command.Parameters.AddWithValue("signed_amount_transaction", entry.SignedAmountTransaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
          coalesce(jel.description, '') as description
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
}
