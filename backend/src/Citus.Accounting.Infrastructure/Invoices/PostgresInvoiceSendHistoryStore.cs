using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Infrastructure.Persistence;

namespace Citus.Accounting.Infrastructure.Invoices;

public sealed class PostgresInvoiceSendHistoryStore : IInvoiceSendHistoryStore
{
    private readonly PostgresConnectionFactory _connections;

    public PostgresInvoiceSendHistoryStore(PostgresConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            create table if not exists invoice_send_history (
              id uuid primary key default gen_random_uuid(),
              company_id uuid not null,
              invoice_id uuid not null,
              sent_at timestamptz not null default now(),
              sent_by_user_id uuid not null,
              to_email text not null,
              cc_emails text not null default '',
              bcc_emails text not null default '',
              subject text not null,
              status text not null,
              error_message text,
              constraint invoice_send_history_status_chk check (status in ('sent', 'failed'))
            );

            create index if not exists invoice_send_history_invoice_idx
              on invoice_send_history (invoice_id, sent_at desc);
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<InvoiceSendHistoryRecord> RecordAsync(
        InvoiceSendHistoryDraft draft,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into invoice_send_history
              (company_id, invoice_id, sent_by_user_id, to_email, cc_emails,
               bcc_emails, subject, status, error_message)
            values
              (@company_id, @invoice_id, @sent_by, @to_email, @cc, @bcc,
               @subject, @status, @error_message)
            returning id, sent_at;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", draft.CompanyId);
        command.Parameters.AddWithValue("invoice_id", draft.InvoiceId);
        command.Parameters.AddWithValue("sent_by", draft.SentByUserId);
        command.Parameters.AddWithValue("to_email", draft.ToEmail);
        command.Parameters.AddWithValue("cc", draft.CcEmails ?? string.Empty);
        command.Parameters.AddWithValue("bcc", draft.BccEmails ?? string.Empty);
        command.Parameters.AddWithValue("subject", draft.Subject);
        command.Parameters.AddWithValue("status", draft.Status);
        command.Parameters.AddWithValue("error_message",
            (object?)draft.ErrorMessage ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new InvoiceSendHistoryRecord(
            Id: reader.GetGuid(0),
            CompanyId: draft.CompanyId,
            InvoiceId: draft.InvoiceId,
            SentAt: reader.GetFieldValue<DateTimeOffset>(1),
            SentByUserId: draft.SentByUserId,
            ToEmail: draft.ToEmail,
            CcEmails: draft.CcEmails ?? string.Empty,
            BccEmails: draft.BccEmails ?? string.Empty,
            Subject: draft.Subject,
            Status: draft.Status,
            ErrorMessage: draft.ErrorMessage);
    }

    public async Task<IReadOnlyList<InvoiceSendHistoryRecord>> ListByInvoiceAsync(
        CompanyId companyId,
        Guid invoiceId,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, company_id, invoice_id, sent_at, sent_by_user_id,
                   to_email, cc_emails, bcc_emails, subject, status, error_message
              from invoice_send_history
             where company_id = @company_id and invoice_id = @invoice_id
             order by sent_at desc
             limit @limit;
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_id", invoiceId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        var results = new List<InvoiceSendHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new InvoiceSendHistoryRecord(
                Id: reader.GetGuid(0),
                CompanyId: reader.GetGuid(1),
                InvoiceId: reader.GetGuid(2),
                SentAt: reader.GetFieldValue<DateTimeOffset>(3),
                SentByUserId: reader.GetGuid(4),
                ToEmail: reader.GetString(5),
                CcEmails: reader.GetString(6),
                BccEmails: reader.GetString(7),
                Subject: reader.GetString(8),
                Status: reader.GetString(9),
                ErrorMessage: reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return results;
    }
}
