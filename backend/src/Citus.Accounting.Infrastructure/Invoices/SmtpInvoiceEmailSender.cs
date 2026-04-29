using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Citus.Accounting.Application.Invoices;
using Citus.Platform.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Citus.Accounting.Infrastructure.Invoices;

/// <summary>
/// SMTP sender for invoice email. Reuses the platform's
/// <see cref="PlatformEmailDeliveryOptions"/> so operators configure SMTP
/// in one place (PlatformNotifications:Smtp) and both verification mail
/// (sysadmin) and business invoice mail share the same FromEmail / SMTP
/// server / credentials.
///
/// Sends multi-part email — text/plain alternate view + text/html
/// alternate view — with a single PDF attachment. Renders multiple
/// recipients from the operator-supplied To / Cc / Bcc.
/// </summary>
public sealed class SmtpInvoiceEmailSender : IInvoiceEmailSender
{
    private readonly IOptions<PlatformEmailDeliveryOptions> _options;
    private readonly ILogger<SmtpInvoiceEmailSender> _logger;

    public SmtpInvoiceEmailSender(
        IOptions<PlatformEmailDeliveryOptions> options,
        ILogger<SmtpInvoiceEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<InvoiceEmailSendResult> SendAsync(
        InvoiceEmailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var configurationError = _options.Value.GetConfigurationError();
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            return new InvoiceEmailSendResult(false, configurationError);
        }

        var current = _options.Value;
        var smtp = current.Smtp;

        using var mail = new MailMessage
        {
            From = new MailAddress(current.FromEmail.Trim(), current.FromDisplayName.Trim()),
            Subject = request.Subject,
        };

        var displayName = string.IsNullOrWhiteSpace(request.ToDisplayName)
            ? request.ToEmail.Trim()
            : request.ToDisplayName.Trim();
        mail.To.Add(new MailAddress(request.ToEmail.Trim(), displayName));

        foreach (var cc in request.CcEmails)
        {
            if (!string.IsNullOrWhiteSpace(cc))
            {
                mail.CC.Add(cc.Trim());
            }
        }

        foreach (var bcc in request.BccEmails)
        {
            if (!string.IsNullOrWhiteSpace(bcc))
            {
                mail.Bcc.Add(bcc.Trim());
            }
        }

        // Multi-part: plain-text alternate view first, HTML alternate view
        // second. Mail clients that prefer HTML pick the HTML; clients that
        // can't render HTML fall back to the plain text.
        var plainView = AlternateView.CreateAlternateViewFromString(
            request.PlainTextBody,
            new ContentType("text/plain; charset=utf-8"));
        var htmlView = AlternateView.CreateAlternateViewFromString(
            request.HtmlBody,
            new ContentType("text/html; charset=utf-8"));
        mail.AlternateViews.Add(plainView);
        mail.AlternateViews.Add(htmlView);

        var attachmentStream = new MemoryStream(request.AttachmentBytes, writable: false);
        var attachment = new Attachment(attachmentStream, request.AttachmentFileName, "application/pdf");
        mail.Attachments.Add(attachment);

        using var client = new SmtpClient(smtp.Host.Trim(), smtp.Port)
        {
            EnableSsl = smtp.UseSsl,
            Credentials = new NetworkCredential(smtp.Username.Trim(), smtp.Password),
        };

        try
        {
            await client.SendMailAsync(mail, cancellationToken);
            _logger.LogInformation(
                "Invoice email sent: subject='{Subject}' to='{To}' cc={CcCount} bcc={BccCount} attachment={FileName}.",
                request.Subject,
                request.ToEmail,
                mail.CC.Count,
                mail.Bcc.Count,
                request.AttachmentFileName);
            return new InvoiceEmailSendResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Invoice email send failed: subject='{Subject}' to='{To}'.",
                request.Subject,
                request.ToEmail);
            return new InvoiceEmailSendResult(false, ex.Message);
        }
    }
}
