using Modules.GL.JournalEntry;
using Npgsql;

namespace Infrastructure.PostgreSQL.GL;

public sealed class PostgreSqlManualJournalSourceReviewStore : IManualJournalSourceReviewStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlManualJournalSourceReviewStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<ManualJournalSourceReview?> GetAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        ManualJournalSourceReview? review = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select
                  mj.id,
                  mj.company_id,
                  mj.entity_number,
                  mj.display_number,
                  mj.status,
                  mj.entry_date,
                  mj.transaction_currency_code,
                  mj.base_currency_code,
                  mj.fx_rate_snapshot_id,
                  mj.fx_rate,
                  mj.fx_requested_date,
                  mj.fx_effective_date,
                  mj.fx_source,
                  mj.memo,
                  mj.posted_at,
                  mj.created_by_user_id,
                  je.id as linked_journal_entry_id,
                  je.display_number as linked_journal_display_number
                from manual_journal_documents mj
                left join lateral (
                  select id, display_number
                  from journal_entries
                  where company_id = mj.company_id
                    and source_type = 'manual_journal'
                    and source_id = mj.id
                  order by coalesce(posted_at, created_at) desc
                  limit 1
                ) je on true
                where mj.company_id = @company_id
                  and mj.id = @document_id
                limit 1;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                review = new ManualJournalSourceReview
                {
                    Id = reader.GetGuid(reader.GetOrdinal("id")),
                    CompanyId = reader.GetGuid(reader.GetOrdinal("company_id")),
                    EntityNumber = reader.GetString(reader.GetOrdinal("entity_number")),
                    DisplayNumber = reader.GetString(reader.GetOrdinal("display_number")),
                    Status = reader.GetString(reader.GetOrdinal("status")),
                    EntryDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("entry_date")),
                    TransactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                    BaseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code")),
                    FxSnapshotId = reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id")),
                    FxRate = reader.GetDecimal(reader.GetOrdinal("fx_rate")),
                    FxRequestedDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_requested_date")),
                    FxEffectiveDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_effective_date")),
                    FxSource = reader.GetString(reader.GetOrdinal("fx_source")),
                    Memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("memo")),
                    PostedAt = reader.IsDBNull(reader.GetOrdinal("posted_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                    CreatedByUserId = reader.GetGuid(reader.GetOrdinal("created_by_user_id")),
                    LinkedJournalEntryId = reader.IsDBNull(reader.GetOrdinal("linked_journal_entry_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("linked_journal_entry_id")),
                    LinkedJournalDisplayNumber = reader.IsDBNull(reader.GetOrdinal("linked_journal_display_number"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("linked_journal_display_number"))
                };
            }
        }

        if (review is null)
        {
            return null;
        }

        var lines = new List<ManualJournalSourceReviewLine>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select
                  mjl.id,
                  mjl.line_number,
                  mjl.account_id,
                  a.code,
                  a.name,
                  a.root_type,
                  a.detail_type,
                  mjl.description,
                  mjl.tx_debit,
                  mjl.tx_credit
                from manual_journal_document_lines mjl
                inner join accounts a
                  on a.company_id = mjl.company_id
                 and a.id = mjl.account_id
                where mjl.company_id = @company_id
                  and mjl.manual_journal_document_id = @document_id
                order by mjl.line_number asc;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new ManualJournalSourceReviewLine
                {
                    Id = reader.GetGuid(reader.GetOrdinal("id")),
                    LineNumber = reader.GetInt32(reader.GetOrdinal("line_number")),
                    AccountId = reader.GetGuid(reader.GetOrdinal("account_id")),
                    AccountCode = reader.GetString(reader.GetOrdinal("code")),
                    AccountName = reader.GetString(reader.GetOrdinal("name")),
                    RootType = reader.GetString(reader.GetOrdinal("root_type")),
                    DetailType = reader.GetString(reader.GetOrdinal("detail_type")),
                    Description = reader.IsDBNull(reader.GetOrdinal("description"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("description")),
                    TransactionDebit = reader.GetDecimal(reader.GetOrdinal("tx_debit")),
                    TransactionCredit = reader.GetDecimal(reader.GetOrdinal("tx_credit"))
                });
            }
        }

        var relatedEntries = await LoadRelatedEntriesAsync(connection, companyId, review.Id, cancellationToken);

        return review with
        {
            Lines = lines,
            RelatedEntries = relatedEntries
        };
    }

    private static async Task<IReadOnlyList<JournalEntryRelatedEntry>> LoadRelatedEntriesAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var items = new List<JournalEntryRelatedEntry>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              display_number,
              status,
              source_type,
              posted_at
            from journal_entries
            where company_id = @company_id
              and source_id = @source_id
            order by coalesce(posted_at, created_at) asc, display_number asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new JournalEntryRelatedEntry(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("source_type")),
                reader.IsDBNull(reader.GetOrdinal("posted_at"))
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at"))));
        }

        return items;
    }
}
