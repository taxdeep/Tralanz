namespace Citus.Accounting.Application.Invoices;

public interface IInvoiceEmailSender
{
    Task<InvoiceEmailSendResult> SendAsync(
        InvoiceEmailRequest request,
        CancellationToken cancellationToken);
}

public sealed record InvoiceEmailRequest(
    string ToEmail,
    string ToDisplayName,
    IReadOnlyList<string> CcEmails,
    IReadOnlyList<string> BccEmails,
    string Subject,
    string HtmlBody,
    string PlainTextBody,
    string AttachmentFileName,
    byte[] AttachmentBytes);

public sealed record InvoiceEmailSendResult(bool Succeeded, string? ErrorMessage);
