namespace Citus.Accounting.Application.Invoices;

/// <summary>
/// Append-only ledger of invoice email send attempts. Each row captures
/// the recipient, the operator who triggered the send, and the SMTP
/// outcome (sent / failed). Lives separate from the invoice document
/// itself so that the posting-engine schema stays untouched and the
/// audit trail survives even when send fails repeatedly.
/// </summary>
public interface IInvoiceSendHistoryStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<InvoiceSendHistoryRecord> RecordAsync(
        InvoiceSendHistoryDraft draft,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InvoiceSendHistoryRecord>> ListByInvoiceAsync(
        CompanyId companyId,
        Guid invoiceId,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record InvoiceSendHistoryDraft(
    CompanyId CompanyId,
    Guid InvoiceId,
    UserId SentByUserId,
    string ToEmail,
    string CcEmails,
    string BccEmails,
    string Subject,
    string Status,
    string? ErrorMessage);

public sealed record InvoiceSendHistoryRecord(
    Guid Id,
    CompanyId CompanyId,
    Guid InvoiceId,
    DateTimeOffset SentAt,
    UserId SentByUserId,
    string ToEmail,
    string CcEmails,
    string BccEmails,
    string Subject,
    string Status,
    string? ErrorMessage);
