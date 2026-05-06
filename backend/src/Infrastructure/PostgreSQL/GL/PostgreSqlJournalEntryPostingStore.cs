using Modules.GL.JournalEntry;
using Npgsql;
using NpgsqlTypes;
using Infrastructure.PostgreSQL.Numbering;

namespace Infrastructure.PostgreSQL.GL;

public sealed class PostgreSqlJournalEntryPostingStore : IJournalEntryPostingStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly Engines.Numbering.JournalEntry.IJournalEntryNumberLookup _journalEntryNumberLookup;

    public PostgreSqlJournalEntryPostingStore(
        PostgreSqlConnectionFactory connections,
        Engines.Numbering.JournalEntry.IJournalEntryNumberLookup journalEntryNumberLookup)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _journalEntryNumberLookup = journalEntryNumberLookup ?? throw new ArgumentNullException(nameof(journalEntryNumberLookup));
    }

    public async Task<JournalEntryPostResult> PostAsync(
        JournalEntryDraft draft,
        UserId userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.DocumentId is null)
        {
            throw new JournalEntryWorkflowException("not_found", "A saved manual journal draft is required before posting.");
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var lockedSource = await LockManualJournalSourceAsync(
            connection,
            transaction,
            draft.CompanyId,
            draft.DocumentId.Value,
            cancellationToken);

        var existing = await TryFindExistingAsync(
            connection,
            transaction,
            draft.CompanyId,
            draft.DocumentId.Value,
            lockedSource.DisplayNumber,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        await EnsurePostingPeriodOpenAsync(
            connection,
            transaction,
            draft.CompanyId,
            draft.JournalDate,
            cancellationToken);

        if (!string.Equals(lockedSource.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            throw new JournalEntryWorkflowException("invalid_document_status", "Only draft manual journals can be posted.");
        }

        var journalDisplayNumber = await ReserveJournalDisplayNumberAsync(connection, transaction, draft.CompanyId, cancellationToken);
        var entityNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            draft.CompanyId,
            $"entity-number:all:{draft.JournalDate.Year}",
            $"EN{draft.JournalDate.Year}",
            8,
            await FindEntitySeedNumberAsync(connection, transaction, draft.JournalDate.Year, cancellationToken),
            cancellationToken);

        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var meaningfulLines = draft.Lines.Where(static line => line.HasContent).ToArray();
        var totalTxDebit = meaningfulLines.Sum(line => Round2(line.DebitAmount ?? 0m));
        var totalTxCredit = meaningfulLines.Sum(line => Round2(line.CreditAmount ?? 0m));
        var totalDebit = meaningfulLines.Sum(line => Round2((line.DebitAmount ?? 0m) * draft.FxRate));
        var totalCredit = meaningfulLines.Sum(line => Round2((line.CreditAmount ?? 0m) * draft.FxRate));

        await using (var insertEntryCommand = connection.CreateCommand())
        {
            insertEntryCommand.Transaction = transaction;
            insertEntryCommand.CommandText =
                """
                insert into journal_entries (
                  id,
                  company_id,
                  entity_number,
                  display_number,
                  status,
                  source_type,
                  source_id,
                  transaction_currency_code,
                  base_currency_code,
                  exchange_rate,
                  exchange_rate_date,
                  exchange_rate_source,
                  fx_rate_snapshot_id,
                  total_tx_debit,
                  total_tx_credit,
                  total_debit,
                  total_credit,
                  posting_run_id,
                  idempotency_key,
                  posted_at,
                  created_by_user_id,
                  created_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @display_number,
                  'posted',
                  'manual_journal',
                  @source_id,
                  @transaction_currency_code,
                  @base_currency_code,
                  @exchange_rate,
                  @exchange_rate_date,
                  @exchange_rate_source,
                  @fx_rate_snapshot_id,
                  @total_tx_debit,
                  @total_tx_credit,
                  @total_debit,
                  @total_credit,
                  @posting_run_id,
                  @idempotency_key,
                  @posted_at,
                  @created_by_user_id,
                  now()
                );
                """;
            insertEntryCommand.Parameters.AddWithValue("id", journalEntryId);
            insertEntryCommand.Parameters.AddWithValue("company_id", draft.CompanyId);
            insertEntryCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertEntryCommand.Parameters.AddWithValue("display_number", journalDisplayNumber);
            insertEntryCommand.Parameters.AddWithValue("source_id", draft.DocumentId.Value);
            insertEntryCommand.Parameters.AddWithValue("transaction_currency_code", draft.CurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate", Round10(draft.FxRate));
            insertEntryCommand.Parameters.AddWithValue("exchange_rate_date", draft.FxEffectiveDate == default ? draft.JournalDate : draft.FxEffectiveDate);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate_source", draft.FxSourceSemantics);
            insertEntryCommand.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid)
            {
                TypedValue = draft.FxSnapshotId
            });
            insertEntryCommand.Parameters.AddWithValue("total_tx_debit", Round2(totalTxDebit));
            insertEntryCommand.Parameters.AddWithValue("total_tx_credit", Round2(totalTxCredit));
            insertEntryCommand.Parameters.AddWithValue("total_debit", Round2(totalDebit));
            insertEntryCommand.Parameters.AddWithValue("total_credit", Round2(totalCredit));
            insertEntryCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            insertEntryCommand.Parameters.AddWithValue("idempotency_key", $"manual_journal:{draft.DocumentId.Value}");
            insertEntryCommand.Parameters.AddWithValue("posted_at", postedAt);
            insertEntryCommand.Parameters.AddWithValue("created_by_user_id", userId.Value);
            await insertEntryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in meaningfulLines)
        {
            var txDebit = Round2(line.DebitAmount ?? 0m);
            var txCredit = Round2(line.CreditAmount ?? 0m);
            var baseDebit = Round2(txDebit * draft.FxRate);
            var baseCredit = Round2(txCredit * draft.FxRate);
            var journalEntryLineId = Guid.NewGuid();

            await using (var insertLineCommand = connection.CreateCommand())
            {
                insertLineCommand.Transaction = transaction;
                insertLineCommand.CommandText =
                    """
                    insert into journal_entry_lines (
                      id,
                      company_id,
                      journal_entry_id,
                      line_number,
                      account_id,
                      description,
                      party_type,
                      party_id,
                      tx_debit,
                      tx_credit,
                      debit,
                      credit,
                      tax_component_type,
                      control_role,
                      created_at
                    )
                    values (
                      @id,
                      @company_id,
                      @journal_entry_id,
                      @line_number,
                      @account_id,
                      @description,
                      null,
                      null,
                      @tx_debit,
                      @tx_credit,
                      @debit,
                      @credit,
                      null,
                      null,
                      now()
                    );
                    """;
                insertLineCommand.Parameters.AddWithValue("id", journalEntryLineId);
                insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId);
                insertLineCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
                insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
                insertLineCommand.Parameters.AddWithValue("account_id", line.Account!.AccountId);
                insertLineCommand.Parameters.AddWithValue("description", string.IsNullOrWhiteSpace(line.Description) ? (object)DBNull.Value : line.Description);
                insertLineCommand.Parameters.AddWithValue("tx_debit", txDebit);
                insertLineCommand.Parameters.AddWithValue("tx_credit", txCredit);
                insertLineCommand.Parameters.AddWithValue("debit", baseDebit);
                insertLineCommand.Parameters.AddWithValue("credit", baseCredit);
                await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insertLedgerCommand = connection.CreateCommand();
            insertLedgerCommand.Transaction = transaction;
            insertLedgerCommand.CommandText =
                """
                insert into ledger_entries (
                  id,
                  company_id,
                  journal_entry_id,
                  journal_entry_line_id,
                  posting_date,
                  account_id,
                  debit,
                  credit,
                  transaction_currency_code,
                  tx_debit,
                  tx_credit,
                  created_at
                )
                values (
                  @id,
                  @company_id,
                  @journal_entry_id,
                  @journal_entry_line_id,
                  @posting_date,
                  @account_id,
                  @debit,
                  @credit,
                  @transaction_currency_code,
                  @tx_debit,
                  @tx_credit,
                  now()
                );
                """;
            insertLedgerCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLedgerCommand.Parameters.AddWithValue("company_id", draft.CompanyId);
            insertLedgerCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            insertLedgerCommand.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
            insertLedgerCommand.Parameters.AddWithValue("posting_date", draft.JournalDate);
            insertLedgerCommand.Parameters.AddWithValue("account_id", line.Account!.AccountId);
            insertLedgerCommand.Parameters.AddWithValue("debit", baseDebit);
            insertLedgerCommand.Parameters.AddWithValue("credit", baseCredit);
            insertLedgerCommand.Parameters.AddWithValue("transaction_currency_code", draft.CurrencyCode);
            insertLedgerCommand.Parameters.AddWithValue("tx_debit", txDebit);
            insertLedgerCommand.Parameters.AddWithValue("tx_credit", txCredit);
            await insertLedgerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateSourceCommand = connection.CreateCommand())
        {
            updateSourceCommand.Transaction = transaction;
            updateSourceCommand.CommandText =
                """
                update manual_journal_documents
                set status = 'posted',
                    posted_at = @posted_at,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            updateSourceCommand.Parameters.AddWithValue("posted_at", postedAt);
            updateSourceCommand.Parameters.AddWithValue("id", draft.DocumentId.Value);
            updateSourceCommand.Parameters.AddWithValue("company_id", draft.CompanyId);
            var affectedRows = await updateSourceCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new JournalEntryWorkflowException("invalid_document_status", "The manual journal source document could not be marked as posted.");
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new JournalEntryPostResult(
            draft.DocumentId.Value,
            draft.DocumentNumber,
            journalEntryId,
            journalDisplayNumber);
    }

    private async Task<string> ReserveJournalDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var nextDisplayNumber = await _journalEntryNumberLookup.GetNextDisplayNumberAsync(companyId, cancellationToken);
        var seedNumber = long.Parse(nextDisplayNumber["JE-".Length..]);

        return await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            "journal-entry-display",
            "JE-",
            6,
            seedNumber,
            cancellationToken);
    }

    private static async Task EnsurePostingPeriodOpenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        DateOnly postingDate,
        CancellationToken cancellationToken)
    {
        if (!await BookGovernanceTablesExistAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              s.signal_date,
              s.reference_label
            from company_books b
            inner join company_book_governance_signals s
              on s.company_id = b.company_id
             and s.company_book_id = b.id
             and s.signal_type = 'closed_period'
             and s.signal_date >= @posting_date
            where b.company_id = @company_id
              and b.is_active = true
              and b.is_primary = true
              and b.effective_from <= @posting_date
            order by s.signal_date asc, s.created_at asc, s.id asc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("posting_date", postingDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var closedThrough = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("signal_date"));
        var referenceLabel = reader.IsDBNull(reader.GetOrdinal("reference_label"))
            ? "closed period"
            : reader.GetString(reader.GetOrdinal("reference_label"));
        throw new JournalEntryWorkflowException(
            "posting_period_closed",
            $"Posting date {postingDate:yyyy-MM-dd} is locked by {referenceLabel} through {closedThrough:yyyy-MM-dd}.");
    }

    private static async Task<bool> BookGovernanceTablesExistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select to_regclass('public.company_books') is not null
               and to_regclass('public.company_book_governance_signals') is not null;
            """;
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<LockedManualJournalSource> LockManualJournalSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select display_number, status
            from manual_journal_documents
            where company_id = @company_id
              and id = @source_id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new JournalEntryWorkflowException("not_found", "The manual journal source document does not exist.");
        }

        return new LockedManualJournalSource(
            reader.GetString(reader.GetOrdinal("display_number")),
            reader.GetString(reader.GetOrdinal("status")));
    }

    private static async Task<JournalEntryPostResult?> TryFindExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid sourceId,
        string sourceDisplayNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, display_number
            from journal_entries
            where company_id = @company_id
              and source_type = 'manual_journal'
              and source_id = @source_id
              and status = 'posted'
            order by created_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JournalEntryPostResult(
            sourceId,
            sourceDisplayNumber,
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")));
    }

    private static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with all_entities as (
              select entity_number from manual_journal_documents
              union all
              select entity_number from journal_entries
              union all
              select entity_number from invoices
              union all
              select entity_number from bills
              union all
              select entity_number from credit_notes
              union all
              select entity_number from vendor_credits
              union all
              select entity_number from receive_payments
              union all
              select entity_number from pay_bills
              union all
              select entity_number from fx_revaluation_batches
            )
            select coalesce(
              max(
                case
                  when entity_number ~ '^EN{year}[0-9]+$'
                    then substring(entity_number from 7)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from all_entities;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    private static decimal Round10(decimal value) =>
        Math.Round(value, 10, MidpointRounding.ToEven);

    private sealed record LockedManualJournalSource(string DisplayNumber, string Status);
}
