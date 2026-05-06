using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresManualJournalDocumentRepository : IManualJournalDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresManualJournalDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<ManualJournalDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        Guid id;
        string entityNumber;
        string displayNumber;
        string status;
        DateOnly entryDate;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        Guid? fxSnapshotId;
        decimal fxRate;
        DateOnly fxRequestedDate;
        DateOnly fxEffectiveDate;
        string fxSource;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           d.id,
                           d.entity_number,
                           d.display_number,
                           d.status,
                           d.entry_date,
                           d.transaction_currency_code,
                           d.base_currency_code,
                           d.fx_rate_snapshot_id,
                           d.fx_rate,
                           d.fx_requested_date,
                           d.fx_effective_date,
                           d.fx_source,
                           d.memo
                         from manual_journal_documents d
                         where d.company_id = @company_id
                           and d.id = @document_id
                         limit 1;
                         """))
        {
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            entryDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("entry_date"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            fxSnapshotId = reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id"));
            fxRate = reader.GetDecimal(reader.GetOrdinal("fx_rate"));
            fxRequestedDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_requested_date"));
            fxEffectiveDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_effective_date"));
            fxSource = reader.GetString(reader.GetOrdinal("fx_source"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        var lines = new List<ManualJournalDocumentLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.account_id,
                           l.description,
                           l.tx_debit,
                           l.tx_credit
                         from manual_journal_document_lines l
                         where l.company_id = @company_id
                           and l.manual_journal_document_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var lineNumber = reader.GetInt32(reader.GetOrdinal("line_number"));
                var accountId = reader.GetGuid(reader.GetOrdinal("account_id"));
                var description = reader.IsDBNull(reader.GetOrdinal("description"))
                    ? $"Manual journal line {lineNumber}"
                    : reader.GetString(reader.GetOrdinal("description"));
                var txDebit = reader.GetDecimal(reader.GetOrdinal("tx_debit"));
                var txCredit = reader.GetDecimal(reader.GetOrdinal("tx_credit"));

                lines.Add(new ManualJournalDocumentLine(lineNumber, accountId, description, txDebit, txCredit));
            }
        }

        var transactionCurrency = new CurrencyCode(transactionCurrencyCode);
        var baseCurrency = new CurrencyCode(baseCurrencyCode);
        FxSnapshotRef? fxSnapshot = null;

        if (fxSnapshotId.HasValue || transactionCurrency != baseCurrency || fxRate != 1m)
        {
            fxSnapshot = new FxSnapshotRef(
                fxSnapshotId ?? Guid.Empty,
                baseCurrency,
                transactionCurrency,
                fxRate,
                fxRequestedDate,
                fxEffectiveDate,
                fxSource);
        }

        return new ManualJournalDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(displayNumber),
            status,
            entryDate,
            transactionCurrency,
            baseCurrency,
            fxSnapshot,
            lines,
            memo);
    }

    public Task SaveAsync(
        ManualJournalDocument document,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Manual journal document writes are not yet implemented in the PostgreSQL repository.");
    }
}
