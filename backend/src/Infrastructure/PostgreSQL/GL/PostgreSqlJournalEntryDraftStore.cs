using Modules.GL.JournalEntry;
using Npgsql;
using NpgsqlTypes;
using Infrastructure.PostgreSQL.Numbering;

namespace Infrastructure.PostgreSQL.GL;

public sealed class PostgreSqlJournalEntryDraftStore : IJournalEntryDraftStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlJournalEntryDraftStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<JournalEntryDraftSaveResult> SaveAsync(
        JournalEntryDraft draft,
        UserId userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        var documentNumber = draft.DocumentNumber;
        var year = draft.JournalDate.Year;

        if (draft.DocumentId is null)
        {
            var entityNumber = await PostgreSqlNumberingSequences.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                5,
                await FindEntitySeedNumberAsync(connection, transaction, draft.CompanyId, year, cancellationToken),
                cancellationToken);

            documentNumber = await PostgreSqlNumberingSequences.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "manual-journal-display",
                "MJ-",
                6,
                await FindManualJournalSeedNumberAsync(connection, transaction, draft.CompanyId, cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into manual_journal_documents (
                  id,
                  company_id,
                  entity_number,
                  display_number,
                  status,
                  entry_date,
                  transaction_currency_code,
                  base_currency_code,
                  fx_rate_snapshot_id,
                  fx_rate,
                  fx_requested_date,
                  fx_effective_date,
                  fx_source,
                  memo,
                  posted_at,
                  created_by_user_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @display_number,
                  'draft',
                  @entry_date,
                  @transaction_currency_code,
                  @base_currency_code,
                  @fx_rate_snapshot_id,
                  @fx_rate,
                  @fx_requested_date,
                  @fx_effective_date,
                  @fx_source,
                  @memo,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, documentNumber, entityNumber, userId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update manual_journal_documents
                set entry_date = @entry_date,
                    transaction_currency_code = @transaction_currency_code,
                    base_currency_code = @base_currency_code,
                    fx_rate_snapshot_id = @fx_rate_snapshot_id,
                    fx_rate = @fx_rate,
                    fx_requested_date = @fx_requested_date,
                    fx_effective_date = @fx_effective_date,
                    fx_source = @fx_source,
                    memo = @memo,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            BindHeader(updateCommand, draft, documentId, draft.DocumentNumber, string.Empty, userId, includeIdentity: false);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new JournalEntryWorkflowException("invalid_document_status", "The draft could not be updated. Only draft manual journals can be modified.");
            }

            documentNumber = draft.DocumentNumber;
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from manual_journal_document_lines
                where company_id = @company_id
                  and manual_journal_document_id = @document_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteCommand.Parameters.AddWithValue("document_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in draft.Lines.Where(static line => line.HasContent))
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                """
                insert into manual_journal_document_lines (
                  id,
                  company_id,
                  manual_journal_document_id,
                  line_number,
                  account_id,
                  description,
                  tx_debit,
                  tx_credit,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @manual_journal_document_id,
                  @line_number,
                  @account_id,
                  @description,
                  @tx_debit,
                  @tx_credit,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("manual_journal_document_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("account_id", line.Account!.AccountId);
            insertLineCommand.Parameters.AddWithValue("description", string.IsNullOrWhiteSpace(line.Description) ? (object)DBNull.Value : line.Description);
            insertLineCommand.Parameters.AddWithValue("tx_debit", Round2(line.DebitAmount ?? 0m));
            insertLineCommand.Parameters.AddWithValue("tx_credit", Round2(line.CreditAmount ?? 0m));
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        draft.DocumentId = documentId;
        draft.DocumentNumber = documentNumber;
        draft.Status = "draft";

        return new JournalEntryDraftSaveResult(documentId, documentNumber, "draft");
    }

    private static void BindHeader(
        NpgsqlCommand command,
        JournalEntryDraft draft,
        Guid documentId,
        string documentNumber,
        string entityNumber,
        UserId userId,
        bool includeIdentity = true)
    {
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("display_number", documentNumber);
            command.Parameters.AddWithValue("created_by_user_id", userId.Value);
        }
        command.Parameters.AddWithValue("entry_date", draft.JournalDate);
        command.Parameters.AddWithValue("transaction_currency_code", draft.CurrencyCode);
        command.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid)
        {
            TypedValue = draft.FxSnapshotId
        });
        command.Parameters.AddWithValue("fx_rate", Round2(draft.FxRate));
        command.Parameters.AddWithValue("fx_requested_date", draft.JournalDate);
        command.Parameters.AddWithValue("fx_effective_date", draft.FxEffectiveDate == default ? draft.JournalDate : draft.FxEffectiveDate);
        command.Parameters.AddWithValue("fx_source", draft.FxSourceSemantics);
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo);
    }

    private static async Task<long> FindManualJournalSeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select coalesce(
              max(
                case
                  when display_number ~ '^MJ-[0-9]+$'
                    then substring(display_number from 4)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from manual_journal_documents
            where company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static async Task<long> FindEntitySeedNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            with all_entities as (
              select entity_number from manual_journal_documents where company_id = @company_id
              union all
              select entity_number from journal_entries where company_id = @company_id
              union all
              select entity_number from invoices where company_id = @company_id
              union all
              select entity_number from bills where company_id = @company_id
              union all
              select entity_number from credit_notes where company_id = @company_id
              union all
              select entity_number from vendor_credits where company_id = @company_id
              union all
              select entity_number from receive_payments where company_id = @company_id
              union all
              select entity_number from pay_bills where company_id = @company_id
              union all
              select entity_number from fx_revaluation_batches where company_id = @company_id
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
        command.Parameters.AddWithValue("company_id", companyId.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);
}
