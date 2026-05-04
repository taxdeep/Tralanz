using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresSalesIssueCogsStatusReader : ISalesIssueCogsStatusReader
{
    private const string CogsSourceType = "sales_issue_cogs";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresSalesIssueCogsStatusReader(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<IReadOnlyList<SalesIssueCogsStatusRow>> ListAsync(
        CompanyId companyId,
        int take,
        CancellationToken cancellationToken)
    {
        var capped = Math.Clamp(take, 1, 500);
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // LEFT JOIN to journal_entries surfaces "already posted" without
        // a second query. Cost-layer consumption pre-aggregates per
        // sales-issue so the operator sees the estimated COGS up front
        // and isn't surprised by what the engine actually books.
        await using var command = scope.CreateCommand(
            """
            select
              i.id                              as sales_issue_id,
              i.posting_date                    as posting_date,
              i.source_document_number          as source_document_number,
              coalesce(sum(c.consumed_cost_base), 0) as estimated_cogs_base,
              je.id                             as journal_entry_id,
              je.display_number                 as journal_display_number
            from inventory_documents i
            left join journal_entries je
              on je.company_id = i.company_id
             and je.source_type = @cogs_source_type
             and je.source_id = i.id
            left join inventory_ledger_entries le
              on le.company_id = i.company_id
             and le.source_document_id = i.id
            left join inventory_layer_consumptions c
              on c.company_id = le.company_id
             and c.issue_ledger_entry_id = le.id
            where i.company_id = @company_id
              and i.document_type = 'sales_issue'
              and i.status = 'posted'
            group by i.id, i.posting_date, i.source_document_number, je.id, je.display_number
            order by i.posting_date desc, i.id desc
            limit @take;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("cogs_source_type", CogsSourceType);
        command.Parameters.AddWithValue("take", capped);

        var rows = new List<SalesIssueCogsStatusRow>(capped);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SalesIssueCogsStatusRow(
                SalesIssueDocumentId: reader.GetGuid(0),
                PostingDate: reader.GetFieldValue<DateOnly>(1),
                SourceDocumentNumber: reader.IsDBNull(2) ? null : reader.GetString(2),
                EstimatedCogsBase: reader.GetDecimal(3),
                JournalEntryId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                JournalEntryDisplayNumber: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return rows;
    }
}
