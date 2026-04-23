using Infrastructure.PostgreSQL.Numbering;
using Modules.GL.JournalEntry;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.GL;

public sealed class PostgreSqlJournalEntryLifecycleStore : IJournalEntryLifecycleStore
{
    private readonly PostgreSqlConnectionFactory _connections;
    private readonly Engines.Numbering.JournalEntry.IJournalEntryNumberLookup _journalEntryNumberLookup;

    public PostgreSqlJournalEntryLifecycleStore(
        PostgreSqlConnectionFactory connections,
        Engines.Numbering.JournalEntry.IJournalEntryNumberLookup journalEntryNumberLookup)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _journalEntryNumberLookup = journalEntryNumberLookup ?? throw new ArgumentNullException(nameof(journalEntryNumberLookup));
    }

    public Task<JournalEntryLifecycleResult> VoidAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            companyId,
            journalEntryId,
            userId,
            "voided",
            "manual_journal_void",
            "Void",
            cancellationToken);

    public Task<JournalEntryLifecycleResult> ReverseAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            companyId,
            journalEntryId,
            userId,
            "reversed",
            null,
            "Reversal",
            cancellationToken);

    private async Task<JournalEntryLifecycleResult> ApplyLifecycleAsync(
        Guid companyId,
        Guid journalEntryId,
        Guid userId,
        string originalStatus,
        string? compensationSourceType,
        string actionLabel,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureJournalEntryLineAuditColumnsAsync(connection, transaction, cancellationToken);

        var original = await LoadOriginalAsync(connection, transaction, companyId, journalEntryId, cancellationToken)
            ?? throw new InvalidOperationException("The journal entry could not be found in the active company context.");

        var lifecycleBehavior = ResolveLifecycleBehavior(original.SourceType, originalStatus, compensationSourceType);

        if (!string.Equals(original.Status, "posted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only posted journal entries can be voided or reversed.");
        }

        var existingCompensation = await TryFindExistingCompensationAsync(
            connection,
            transaction,
            companyId,
            original.SourceId,
            lifecycleBehavior.CompensationSourceType,
            cancellationToken);

        if (existingCompensation is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new JournalEntryLifecycleResult(
                original.Id,
                original.DisplayNumber,
                originalStatus,
                existingCompensation.PostedAt ?? DateTimeOffset.UtcNow,
                existingCompensation.Id,
                existingCompensation.DisplayNumber,
                lifecycleBehavior.CompensationSourceType);
        }

        var originalLines = await LoadOriginalLinesAsync(connection, transaction, companyId, journalEntryId, cancellationToken);
        if (originalLines.Count == 0)
        {
            throw new InvalidOperationException("A journal entry without lines cannot be voided or reversed.");
        }

        var lifecycleAt = DateTimeOffset.UtcNow;
        var compensationJournalEntryId = Guid.NewGuid();
        var compensationDisplayNumber = await ReserveJournalDisplayNumberAsync(connection, transaction, companyId, cancellationToken);
        var compensationEntityNumber = await PostgreSqlNumberingSequences.ReserveAsync(
            connection,
            transaction,
            companyId,
            $"entity-number:all:{original.EntryDate.Year}",
            $"EN{original.EntryDate.Year}",
            8,
            await FindEntitySeedNumberAsync(connection, transaction, original.EntryDate.Year, cancellationToken),
            cancellationToken);

        var totalTxDebit = originalLines.Sum(line => Round2(line.TransactionCredit));
        var totalTxCredit = originalLines.Sum(line => Round2(line.TransactionDebit));
        var totalDebit = originalLines.Sum(line => Round2(line.Credit));
        var totalCredit = originalLines.Sum(line => Round2(line.Debit));

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
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
                  @source_type,
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
            command.Parameters.AddWithValue("id", compensationJournalEntryId);
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("entity_number", compensationEntityNumber);
            command.Parameters.AddWithValue("display_number", compensationDisplayNumber);
            command.Parameters.AddWithValue("source_type", lifecycleBehavior.CompensationSourceType);
            command.Parameters.AddWithValue("source_id", original.SourceId);
            command.Parameters.AddWithValue("transaction_currency_code", original.TransactionCurrencyCode);
            command.Parameters.AddWithValue("base_currency_code", original.BaseCurrencyCode);
            command.Parameters.AddWithValue("exchange_rate", original.ExchangeRate);
            command.Parameters.AddWithValue("exchange_rate_date", original.ExchangeRateDate);
            command.Parameters.AddWithValue("exchange_rate_source", original.ExchangeRateSource);
            command.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid)
            {
                TypedValue = original.FxSnapshotId
            });
            command.Parameters.AddWithValue("total_tx_debit", totalTxDebit);
            command.Parameters.AddWithValue("total_tx_credit", totalTxCredit);
            command.Parameters.AddWithValue("total_debit", totalDebit);
            command.Parameters.AddWithValue("total_credit", totalCredit);
            command.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            command.Parameters.AddWithValue("idempotency_key", $"{original.SourceType}:{original.SourceId:D}:{lifecycleBehavior.CompensationSourceType}");
            command.Parameters.AddWithValue("posted_at", lifecycleAt);
            command.Parameters.AddWithValue("created_by_user_id", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in originalLines)
        {
            var compensationLineId = Guid.NewGuid();
            var txDebit = Round2(line.TransactionCredit);
            var txCredit = Round2(line.TransactionDebit);
            var debit = Round2(line.Credit);
            var credit = Round2(line.Debit);
            var compensationDescription = BuildCompensationDescription(actionLabel, original.DisplayNumber, line.Description);

            await using (var lineCommand = connection.CreateCommand())
            {
                lineCommand.Transaction = transaction;
                lineCommand.CommandText =
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
                      posting_role,
                      source_line_number,
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
                      @party_id,
                      @tx_debit,
                      @tx_credit,
                      @debit,
                      @credit,
                      @tax_component_type,
                      @control_role,
                      @posting_role,
                      @source_line_number,
                      now()
                    );
                    """;
                lineCommand.Parameters.AddWithValue("id", compensationLineId);
                lineCommand.Parameters.AddWithValue("company_id", companyId);
                lineCommand.Parameters.AddWithValue("journal_entry_id", compensationJournalEntryId);
                lineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
                lineCommand.Parameters.AddWithValue("account_id", line.AccountId);
                lineCommand.Parameters.AddWithValue("description", (object?)compensationDescription ?? DBNull.Value);
                lineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("party_id", NpgsqlDbType.Uuid)
                {
                    TypedValue = line.PartyId
                });
                lineCommand.Parameters.AddWithValue("tx_debit", txDebit);
                lineCommand.Parameters.AddWithValue("tx_credit", txCredit);
                lineCommand.Parameters.AddWithValue("debit", debit);
                lineCommand.Parameters.AddWithValue("credit", credit);
                lineCommand.Parameters.AddWithValue("tax_component_type", (object?)line.TaxComponentType ?? DBNull.Value);
                lineCommand.Parameters.AddWithValue("control_role", (object?)line.ControlRole ?? DBNull.Value);
                lineCommand.Parameters.AddWithValue("posting_role", (object?)line.PostingRole ?? DBNull.Value);
                lineCommand.Parameters.AddWithValue("source_line_number", (object?)line.SourceLineNumber ?? DBNull.Value);
                await lineCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var ledgerCommand = connection.CreateCommand();
            ledgerCommand.Transaction = transaction;
            ledgerCommand.CommandText =
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
            ledgerCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            ledgerCommand.Parameters.AddWithValue("company_id", companyId);
            ledgerCommand.Parameters.AddWithValue("journal_entry_id", compensationJournalEntryId);
            ledgerCommand.Parameters.AddWithValue("journal_entry_line_id", compensationLineId);
            ledgerCommand.Parameters.AddWithValue("posting_date", original.EntryDate);
            ledgerCommand.Parameters.AddWithValue("account_id", line.AccountId);
            ledgerCommand.Parameters.AddWithValue("debit", debit);
            ledgerCommand.Parameters.AddWithValue("credit", credit);
            ledgerCommand.Parameters.AddWithValue("transaction_currency_code", original.TransactionCurrencyCode);
            ledgerCommand.Parameters.AddWithValue("tx_debit", txDebit);
            ledgerCommand.Parameters.AddWithValue("tx_credit", txCredit);
            await ledgerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var timestampColumn = originalStatus == "voided" ? "voided_at" : "reversed_at";

        await using (var updateJournalCommand = connection.CreateCommand())
        {
            updateJournalCommand.Transaction = transaction;
            updateJournalCommand.CommandText =
                $"""
                update journal_entries
                set status = @status,
                    {timestampColumn} = @lifecycle_at
                where id = @id
                  and company_id = @company_id
                  and status = 'posted';
                """;
            updateJournalCommand.Parameters.AddWithValue("status", originalStatus);
            updateJournalCommand.Parameters.AddWithValue("lifecycle_at", lifecycleAt);
            updateJournalCommand.Parameters.AddWithValue("id", original.Id);
            updateJournalCommand.Parameters.AddWithValue("company_id", companyId);
            var affectedRows = await updateJournalCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException("The original journal entry could not be moved to the requested lifecycle state.");
            }
        }

        if (lifecycleBehavior.UpdateSourceDocumentStatus)
        {
            await using var updateSourceCommand = connection.CreateCommand();
            updateSourceCommand.Transaction = transaction;
            updateSourceCommand.CommandText =
                """
                update manual_journal_documents
                set status = @status,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'posted';
                """;
            updateSourceCommand.Parameters.AddWithValue("status", originalStatus);
            updateSourceCommand.Parameters.AddWithValue("id", original.SourceId);
            updateSourceCommand.Parameters.AddWithValue("company_id", companyId);
            var affectedRows = await updateSourceCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException("The manual journal source document could not be moved to the requested lifecycle state.");
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new JournalEntryLifecycleResult(
            original.Id,
            original.DisplayNumber,
            originalStatus,
            lifecycleAt,
            compensationJournalEntryId,
            compensationDisplayNumber,
            lifecycleBehavior.CompensationSourceType);
    }

    private async Task<string> ReserveJournalDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
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
              select entity_number from credit_applications
              union all
              select entity_number from vendor_credit_applications
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

    private static async Task<LifecycleHeader?> LoadOriginalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              je.id,
              je.display_number,
              je.status,
              je.source_type,
              je.source_id,
              coalesce(
                mj.entry_date,
                i.invoice_date,
                cn.credit_note_date,
                b.bill_date,
                vc.vendor_credit_date,
                rp.payment_date,
                ca.application_date,
                pb.payment_date,
                vca.application_date
              ) as entry_date,
              je.transaction_currency_code,
              je.base_currency_code,
              je.exchange_rate,
              je.exchange_rate_date,
              je.exchange_rate_source,
              je.fx_rate_snapshot_id
            from journal_entries je
            left join manual_journal_documents mj
              on mj.company_id = je.company_id
             and mj.id = je.source_id
             and je.source_type = 'manual_journal'
            left join invoices i
              on i.company_id = je.company_id
             and i.id = je.source_id
             and je.source_type = 'invoice'
            left join credit_notes cn
              on cn.company_id = je.company_id
             and cn.id = je.source_id
             and je.source_type = 'credit_note'
            left join bills b
              on b.company_id = je.company_id
             and b.id = je.source_id
             and je.source_type = 'bill'
            left join vendor_credits vc
              on vc.company_id = je.company_id
             and vc.id = je.source_id
             and je.source_type = 'vendor_credit'
            left join receive_payments rp
              on rp.company_id = je.company_id
             and rp.id = je.source_id
             and je.source_type = 'receive_payment'
            left join credit_applications ca
              on ca.company_id = je.company_id
             and ca.id = je.source_id
             and je.source_type = 'credit_application'
            left join pay_bills pb
              on pb.company_id = je.company_id
             and pb.id = je.source_id
             and je.source_type = 'pay_bill'
            left join vendor_credit_applications vca
              on vca.company_id = je.company_id
             and vca.id = je.source_id
             and je.source_type = 'vendor_credit_application'
            where je.company_id = @company_id
              and je.id = @journal_entry_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LifecycleHeader(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetGuid(reader.GetOrdinal("source_id")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("entry_date")),
            reader.GetString(reader.GetOrdinal("transaction_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetDecimal(reader.GetOrdinal("exchange_rate")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("exchange_rate_date")),
            reader.GetString(reader.GetOrdinal("exchange_rate_source")),
            reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id")));
    }

    private static async Task<IReadOnlyList<LifecycleLine>> LoadOriginalLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        var lines = new List<LifecycleLine>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select
              line_number,
              account_id,
              description,
              tx_debit,
              tx_credit,
              debit,
              credit,
              tax_component_type,
              control_role,
              party_id,
              posting_role,
              source_line_number
            from journal_entry_lines
            where company_id = @company_id
              and journal_entry_id = @journal_entry_id
            order by line_number asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new LifecycleLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                reader.GetGuid(reader.GetOrdinal("account_id")),
                reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
                reader.GetDecimal(reader.GetOrdinal("tx_debit")),
                reader.GetDecimal(reader.GetOrdinal("tx_credit")),
                reader.GetDecimal(reader.GetOrdinal("debit")),
                reader.GetDecimal(reader.GetOrdinal("credit")),
                reader.IsDBNull(reader.GetOrdinal("tax_component_type")) ? null : reader.GetString(reader.GetOrdinal("tax_component_type")),
                reader.IsDBNull(reader.GetOrdinal("control_role")) ? null : reader.GetString(reader.GetOrdinal("control_role")),
                reader.IsDBNull(reader.GetOrdinal("party_id")) ? null : reader.GetGuid(reader.GetOrdinal("party_id")),
                reader.IsDBNull(reader.GetOrdinal("posting_role")) ? null : reader.GetString(reader.GetOrdinal("posting_role")),
                reader.IsDBNull(reader.GetOrdinal("source_line_number")) ? null : reader.GetInt32(reader.GetOrdinal("source_line_number"))));
        }

        return lines;
    }

    private static async Task EnsureJournalEntryLineAuditColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            alter table journal_entry_lines
              add column if not exists posting_role text null,
              add column if not exists source_line_number integer null;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LifecycleCompensation?> TryFindExistingCompensationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid sourceId,
        string compensationSourceType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select id, display_number, posted_at
            from journal_entries
            where company_id = @company_id
              and source_id = @source_id
              and source_type = @source_type
            order by coalesce(posted_at, created_at) desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("source_type", compensationSourceType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LifecycleCompensation(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("display_number")),
            reader.IsDBNull(reader.GetOrdinal("posted_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")));
    }

    private static string? BuildCompensationDescription(
        string actionLabel,
        string originalDisplayNumber,
        string? originalDescription)
    {
        var prefix = $"{actionLabel} of {originalDisplayNumber}";
        return string.IsNullOrWhiteSpace(originalDescription)
            ? prefix
            : $"{prefix}: {originalDescription}";
    }

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    private static LifecycleBehavior ResolveLifecycleBehavior(
        string sourceType,
        string originalStatus,
        string? requestedCompensationSourceType)
    {
        var normalizedSourceType = sourceType.Trim().ToLowerInvariant();
        var normalizedOriginalStatus = originalStatus.Trim().ToLowerInvariant();

        if (normalizedOriginalStatus == "voided")
        {
            if (!string.Equals(normalizedSourceType, "manual_journal", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Void is currently supported only for manual-journal sourced entries.");
            }

            return new LifecycleBehavior(requestedCompensationSourceType ?? "manual_journal_void", true);
        }

        if (normalizedOriginalStatus != "reversed")
        {
            throw new InvalidOperationException($"Unsupported journal-entry lifecycle state '{originalStatus}'.");
        }

        return normalizedSourceType switch
        {
            "manual_journal" => new LifecycleBehavior(requestedCompensationSourceType ?? "manual_journal_reversal", true),
            "invoice" => new LifecycleBehavior("invoice_reversal", false),
            "credit_note" => new LifecycleBehavior("credit_note_reversal", false),
            "bill" => new LifecycleBehavior("bill_reversal", false),
            "vendor_credit" => new LifecycleBehavior("vendor_credit_reversal", false),
            "receive_payment" => new LifecycleBehavior("receive_payment_reversal", false),
            "credit_application" => new LifecycleBehavior("credit_application_reversal", false),
            "pay_bill" => new LifecycleBehavior("pay_bill_reversal", false),
            "vendor_credit_application" => new LifecycleBehavior("vendor_credit_application_reversal", false),
            _ => throw new InvalidOperationException(
                $"Reversal is not supported for journal entries sourced from '{sourceType}'.")
        };
    }

    private sealed record class LifecycleHeader(
        Guid Id,
        string DisplayNumber,
        string Status,
        string SourceType,
        Guid SourceId,
        DateOnly EntryDate,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal ExchangeRate,
        DateOnly ExchangeRateDate,
        string ExchangeRateSource,
        Guid? FxSnapshotId);

    private sealed record class LifecycleLine(
        int LineNumber,
        Guid AccountId,
        string? Description,
        decimal TransactionDebit,
        decimal TransactionCredit,
        decimal Debit,
        decimal Credit,
        string? TaxComponentType,
        string? ControlRole,
        Guid? PartyId,
        string? PostingRole,
        int? SourceLineNumber);

    private sealed record class LifecycleCompensation(
        Guid Id,
        string DisplayNumber,
        DateTimeOffset? PostedAt);

    private sealed record class LifecycleBehavior(
        string CompensationSourceType,
        bool UpdateSourceDocumentStatus);
}
