using Modules.GL.JournalEntry;
using Npgsql;

namespace Infrastructure.PostgreSQL.GL;

public sealed class PostgreSqlJournalEntryReviewStore : IJournalEntryReviewStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlJournalEntryReviewStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<JournalEntryReview?> GetAsync(
        Guid companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);

        JournalEntryReview? review = null;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select
                  je.id,
                  je.company_id,
                  je.entity_number,
                  je.display_number,
                  je.status,
                  je.source_type,
                  je.source_id,
                  je.transaction_currency_code,
                  je.base_currency_code,
                  je.exchange_rate,
                  je.exchange_rate_date,
                  je.exchange_rate_source,
                  je.fx_rate_snapshot_id,
                  fx.rate_type,
                  fx.quote_basis,
                  fx.rate_use_case,
                  fx.posting_reason,
                  fx.snapshot_semantics,
                  fx.row_origin,
                  fx.provider_key,
                  je.total_tx_debit,
                  je.total_tx_credit,
                  je.total_debit,
                  je.total_credit,
                  je.posted_at,
                  je.voided_at,
                  je.reversed_at,
                  je.created_by_user_id,
                  count(jel.id)::int as line_count
                from journal_entries je
                left join company_fx_rate_snapshots fx
                  on fx.company_id = je.company_id
                 and fx.id = je.fx_rate_snapshot_id
                left join journal_entry_lines jel
                  on jel.company_id = je.company_id
                 and jel.journal_entry_id = je.id
                where je.company_id = @company_id
                  and je.id = @journal_entry_id
                group by
                  je.id,
                  je.company_id,
                  je.entity_number,
                  je.display_number,
                  je.status,
                  je.source_type,
                  je.source_id,
                  je.transaction_currency_code,
                  je.base_currency_code,
                  je.exchange_rate,
                  je.exchange_rate_date,
                  je.exchange_rate_source,
                  je.fx_rate_snapshot_id,
                  fx.rate_type,
                  fx.quote_basis,
                  fx.rate_use_case,
                  fx.posting_reason,
                  fx.snapshot_semantics,
                  fx.row_origin,
                  fx.provider_key,
                  je.total_tx_debit,
                  je.total_tx_credit,
                  je.total_debit,
                  je.total_credit,
                  je.posted_at,
                  je.voided_at,
                  je.reversed_at,
                  je.created_by_user_id
                limit 1;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                review = new JournalEntryReview
                {
                    Id = reader.GetGuid(reader.GetOrdinal("id")),
                    CompanyId = reader.GetGuid(reader.GetOrdinal("company_id")),
                    EntityNumber = reader.GetString(reader.GetOrdinal("entity_number")),
                    DisplayNumber = reader.GetString(reader.GetOrdinal("display_number")),
                    Status = reader.GetString(reader.GetOrdinal("status")),
                    SourceType = reader.GetString(reader.GetOrdinal("source_type")),
                    SourceId = reader.GetGuid(reader.GetOrdinal("source_id")),
                    TransactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                    BaseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code")),
                    ExchangeRate = reader.GetDecimal(reader.GetOrdinal("exchange_rate")),
                    ExchangeRateDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("exchange_rate_date")),
                    ExchangeRateSource = reader.GetString(reader.GetOrdinal("exchange_rate_source")),
                    FxSnapshotId = reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id")),
                    FxRateType = reader.IsDBNull(reader.GetOrdinal("rate_type"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("rate_type")),
                    FxQuoteBasis = reader.IsDBNull(reader.GetOrdinal("quote_basis"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("quote_basis")),
                    FxRateUseCase = reader.IsDBNull(reader.GetOrdinal("rate_use_case"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("rate_use_case")),
                    FxPostingReason = reader.IsDBNull(reader.GetOrdinal("posting_reason"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("posting_reason")),
                    FxSnapshotSemantics = reader.IsDBNull(reader.GetOrdinal("snapshot_semantics"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("snapshot_semantics")),
                    FxSnapshotRowOrigin = reader.IsDBNull(reader.GetOrdinal("row_origin"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("row_origin")),
                    FxProviderKey = reader.IsDBNull(reader.GetOrdinal("provider_key"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("provider_key")),
                    TotalTransactionDebit = reader.GetDecimal(reader.GetOrdinal("total_tx_debit")),
                    TotalTransactionCredit = reader.GetDecimal(reader.GetOrdinal("total_tx_credit")),
                    TotalDebit = reader.GetDecimal(reader.GetOrdinal("total_debit")),
                    TotalCredit = reader.GetDecimal(reader.GetOrdinal("total_credit")),
                    LineCount = reader.GetInt32(reader.GetOrdinal("line_count")),
                    PostedAt = reader.IsDBNull(reader.GetOrdinal("posted_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                    VoidedAt = reader.IsDBNull(reader.GetOrdinal("voided_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("voided_at")),
                    ReversedAt = reader.IsDBNull(reader.GetOrdinal("reversed_at"))
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reversed_at")),
                    CreatedByUserId = reader.GetGuid(reader.GetOrdinal("created_by_user_id"))
                };
            }
        }

        if (review is null)
        {
            return null;
        }

        var lines = new List<JournalEntryReviewLine>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                select
                  jel.id,
                  jel.line_number,
                  jel.account_id,
                  a.code,
                  a.name,
                  a.root_type,
                  a.detail_type,
                  a.system_role,
                  a.system_key,
                  jel.description,
                  jel.tx_debit,
                  jel.tx_credit,
                  jel.debit,
                  jel.credit,
                  jel.tax_component_type,
                  jel.control_role,
                  jel.party_id
                from journal_entry_lines jel
                inner join accounts a
                  on a.company_id = jel.company_id
                 and a.id = jel.account_id
                where jel.company_id = @company_id
                  and jel.journal_entry_id = @journal_entry_id
                order by jel.line_number asc;
                """;
            command.Parameters.AddWithValue("company_id", companyId);
            command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new JournalEntryReviewLine
                {
                    Id = reader.GetGuid(reader.GetOrdinal("id")),
                    LineNumber = reader.GetInt32(reader.GetOrdinal("line_number")),
                    AccountId = reader.GetGuid(reader.GetOrdinal("account_id")),
                    AccountCode = reader.GetString(reader.GetOrdinal("code")),
                    AccountName = reader.GetString(reader.GetOrdinal("name")),
                    RootType = reader.GetString(reader.GetOrdinal("root_type")),
                    DetailType = reader.GetString(reader.GetOrdinal("detail_type")),
                    AccountSystemRole = reader.IsDBNull(reader.GetOrdinal("system_role"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("system_role")),
                    AccountSystemKey = reader.IsDBNull(reader.GetOrdinal("system_key"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("system_key")),
                    Description = reader.IsDBNull(reader.GetOrdinal("description"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("description")),
                    TransactionDebit = reader.GetDecimal(reader.GetOrdinal("tx_debit")),
                    TransactionCredit = reader.GetDecimal(reader.GetOrdinal("tx_credit")),
                    Debit = reader.GetDecimal(reader.GetOrdinal("debit")),
                    Credit = reader.GetDecimal(reader.GetOrdinal("credit")),
                    TaxComponentType = reader.IsDBNull(reader.GetOrdinal("tax_component_type"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("tax_component_type")),
                    ControlRole = reader.IsDBNull(reader.GetOrdinal("control_role"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("control_role")),
                    PartyId = reader.IsDBNull(reader.GetOrdinal("party_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("party_id"))
                });
            }
        }

        var relatedEntries = await LoadRelatedEntriesAsync(connection, companyId, review.SourceId, cancellationToken);

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
