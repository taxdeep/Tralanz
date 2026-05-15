using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresJournalEntryReviewRepository : IJournalEntryReviewRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresJournalEntryReviewRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<JournalEntryReviewListItem>> ListRecentAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken)
    {
        var effectiveTake = Math.Clamp(take, 1, 100);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var items = new List<JournalEntryReviewListItem>();

        await using var command = scope.CreateCommand(
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
              je.total_tx_debit,
              je.total_tx_credit,
              je.total_debit,
              je.total_credit,
              je.posted_at,
              je.voided_at,
              je.reversed_at,
              count(jel.id)::int as line_count
            from journal_entries je
            left join journal_entry_lines jel
              on jel.company_id = je.company_id
             and jel.journal_entry_id = je.id
            where je.company_id = @company_id
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
              je.total_tx_debit,
              je.total_tx_credit,
              je.total_debit,
              je.total_credit,
              je.posted_at,
              je.voided_at,
              je.reversed_at,
              je.created_at
            order by coalesce(je.posted_at, je.created_at) desc, je.display_number desc
            limit @take;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("take", effectiveTake);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapListItem(reader));
        }

        return items;
    }

    public async Task<JournalEntryReview?> GetAsync(
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        JournalEntryReview? review = null;

        await using (var command = scope.CreateCommand(
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
                           je.total_tx_debit,
                           je.total_tx_credit,
                           je.total_debit,
                           je.total_credit,
                           je.posted_at,
                           je.voided_at,
                           je.reversed_at,
                           je.created_by_user_id
                         limit 1;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                review = new JournalEntryReview(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
                    reader.GetString(reader.GetOrdinal("entity_number")),
                    reader.GetString(reader.GetOrdinal("display_number")),
                    reader.GetString(reader.GetOrdinal("status")),
                    reader.GetString(reader.GetOrdinal("source_type")),
                    reader.GetGuid(reader.GetOrdinal("source_id")),
                    reader.GetString(reader.GetOrdinal("transaction_currency_code")),
                    reader.GetString(reader.GetOrdinal("base_currency_code")),
                    reader.GetDecimal(reader.GetOrdinal("exchange_rate")),
                    reader.GetFieldValue<DateOnly>(reader.GetOrdinal("exchange_rate_date")),
                    reader.GetString(reader.GetOrdinal("exchange_rate_source")),
                    reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id")) ? null : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id")),
                    reader.GetDecimal(reader.GetOrdinal("total_tx_debit")),
                    reader.GetDecimal(reader.GetOrdinal("total_tx_credit")),
                    reader.GetDecimal(reader.GetOrdinal("total_debit")),
                    reader.GetDecimal(reader.GetOrdinal("total_credit")),
                    reader.GetInt32(reader.GetOrdinal("line_count")),
                    reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                    reader.IsDBNull(reader.GetOrdinal("voided_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("voided_at")),
                    reader.IsDBNull(reader.GetOrdinal("reversed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reversed_at")),
                    UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id"))),
                    Array.Empty<JournalEntryReviewLine>());
            }
        }

        if (review is null)
        {
            return null;
        }

        var lines = new List<JournalEntryReviewLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           jel.id,
                           jel.line_number,
                           jel.account_id,
                           a.code,
                           a.name,
                           a.root_type,
                           a.detail_type,
                           jel.description,
                           jel.tx_debit,
                           jel.tx_credit,
                           jel.debit,
                           jel.credit,
                           jel.tax_component_type,
                           jel.control_role,
                           jel.party_id,
                           jel.posting_role,
                           jel.source_line_number
                         from journal_entry_lines jel
                         inner join accounts a
                           on a.company_id = jel.company_id
                          and a.id = jel.account_id
                         where jel.company_id = @company_id
                           and jel.journal_entry_id = @journal_entry_id
                         order by jel.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new JournalEntryReviewLine(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("code")),
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.GetString(reader.GetOrdinal("root_type")),
                    reader.GetString(reader.GetOrdinal("detail_type")),
                    reader.IsDBNull(reader.GetOrdinal("description")) ? string.Empty : reader.GetString(reader.GetOrdinal("description")),
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
        }

        return review with
        {
            Lines = lines
        };
    }

    public async Task<JournalEntryReviewListItem?> FindBySourceAsync(
        CompanyId companyId,
        string sourceType,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
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
              je.total_tx_debit,
              je.total_tx_credit,
              je.total_debit,
              je.total_credit,
              je.posted_at,
              je.voided_at,
              je.reversed_at,
              count(jel.id)::int as line_count
            from journal_entries je
            left join journal_entry_lines jel
              on jel.company_id = je.company_id
             and jel.journal_entry_id = je.id
            where je.company_id = @company_id
              and je.source_type = @source_type
              and je.source_id = @source_id
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
              je.total_tx_debit,
              je.total_tx_credit,
              je.total_debit,
              je.total_credit,
              je.posted_at,
              je.voided_at,
              je.reversed_at,
              je.created_at
            order by coalesce(je.posted_at, je.created_at) desc, je.display_number desc
            limit 1;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", sourceType.Trim());
        command.Parameters.AddWithValue("source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapListItem(reader)
            : null;
    }

    private static JournalEntryReviewListItem MapListItem(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("display_number")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetString(reader.GetOrdinal("source_type")),
            reader.GetGuid(reader.GetOrdinal("source_id")),
            reader.GetString(reader.GetOrdinal("transaction_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetDecimal(reader.GetOrdinal("total_tx_debit")),
            reader.GetDecimal(reader.GetOrdinal("total_tx_credit")),
            reader.GetDecimal(reader.GetOrdinal("total_debit")),
            reader.GetDecimal(reader.GetOrdinal("total_credit")),
            reader.GetInt32(reader.GetOrdinal("line_count")),
            reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
            reader.IsDBNull(reader.GetOrdinal("voided_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("voided_at")),
            reader.IsDBNull(reader.GetOrdinal("reversed_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("reversed_at")));
}
