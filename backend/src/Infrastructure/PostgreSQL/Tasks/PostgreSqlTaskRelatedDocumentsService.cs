using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;

namespace Infrastructure.PostgreSQL.Tasks;

/// <summary>
/// Single round-trip via a UNION ALL across the four line tables that
/// Batch 8 stamped with <c>task_id</c>. Each branch joins back to its
/// parent doc for header info, sums the line amounts attributed to
/// the task, and projects a uniform shape the API and UI consume.
///
/// Voided / cancelled docs are intentionally INCLUDED — the operator
/// looking at a task should see "this was on credit note CM-007 that
/// reversed invoice INV-12" even when the credit note itself was
/// later voided. The UI badges the status; this service stays purely
/// reportorial.
/// </summary>
public sealed class PostgreSqlTaskRelatedDocumentsService(PostgreSqlConnectionFactory connections) : ITaskRelatedDocumentsService
{
    public async Task<IReadOnlyList<TaskRelatedDocument>> ListForTaskAsync(
        CompanyId companyId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to list related documents.");
        }
        if (taskId == Guid.Empty)
        {
            throw new InvalidOperationException("Task id is required.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            -- Each sub-select narrows by company_id + task_id, then
            -- aggregates the per-task amount via SUM(line_amount).
            -- Header columns vary by table (invoice_date vs bill_date
            -- etc.) so each branch aliases them into a common shape.
            select 'invoice' as document_type,
                   i.id as document_id,
                   i.invoice_number as display_number,
                   i.invoice_date as document_date,
                   i.status,
                   sum(il.line_amount)::numeric as task_amount,
                   i.document_currency_code as currency_code
              from invoice_lines il
              join invoices i
                on i.id = il.invoice_id
               and i.company_id = il.company_id
             where il.company_id = @company_id
               and il.task_id = @task_id
             group by i.id, i.invoice_number, i.invoice_date, i.status, i.document_currency_code

            union all

            select 'credit_note',
                   cn.id,
                   cn.credit_note_number,
                   cn.credit_note_date,
                   cn.status,
                   sum(cnl.line_amount)::numeric,
                   cn.document_currency_code
              from credit_note_lines cnl
              join credit_notes cn
                on cn.id = cnl.credit_note_id
               and cn.company_id = cnl.company_id
             where cnl.company_id = @company_id
               and cnl.task_id = @task_id
             group by cn.id, cn.credit_note_number, cn.credit_note_date, cn.status, cn.document_currency_code

            union all

            select 'bill',
                   b.id,
                   b.bill_number,
                   b.bill_date,
                   b.status,
                   sum(bl.line_amount)::numeric,
                   b.document_currency_code
              from bill_lines bl
              join bills b
                on b.id = bl.bill_id
               and b.company_id = bl.company_id
             where bl.company_id = @company_id
               and bl.task_id = @task_id
             group by b.id, b.bill_number, b.bill_date, b.status, b.document_currency_code

            union all

            -- expense_lines has no company_id of its own; isolate via the
            -- parent expense row, mirroring the margin-report SQL.
            select 'expense',
                   e.id,
                   e.expense_number,
                   e.payment_date,
                   e.status,
                   sum(el.line_total)::numeric,
                   e.transaction_currency_code
              from expense_lines el
              join expenses e
                on e.id = el.expense_id
               and e.company_id = @company_id
             where el.task_id = @task_id
             group by e.id, e.expense_number, e.payment_date, e.status, e.transaction_currency_code

            order by document_date desc, display_number asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("task_id", taskId);

        var rows = new List<TaskRelatedDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var documentType = reader.GetString(reader.GetOrdinal("document_type"));
            var documentId = reader.GetGuid(reader.GetOrdinal("document_id"));
            rows.Add(new TaskRelatedDocument
            {
                DocumentType = documentType,
                DocumentId = documentId,
                DisplayNumber = reader.GetString(reader.GetOrdinal("display_number")),
                DocumentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                TaskAmount = reader.GetDecimal(reader.GetOrdinal("task_amount")),
                CurrencyCode = reader.GetString(reader.GetOrdinal("currency_code")),
                NavigationHref = BuildHref(documentType, documentId),
            });
        }

        return rows;
    }

    private static string BuildHref(string documentType, Guid documentId) => documentType switch
    {
        TaskRelatedDocumentType.Invoice => $"invoices/{documentId:D}",
        TaskRelatedDocumentType.CreditNote => $"credit-memos/{documentId:D}",
        TaskRelatedDocumentType.Bill => $"bills/{documentId:D}",
        TaskRelatedDocumentType.Expense => $"expenses/{documentId:D}",
        _ => string.Empty,
    };
}
