using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Citus.Accounting.Application.Invoices;
using Citus.Platform.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Citus.Accounting.Infrastructure.Invoices;

/// <summary>
/// SMTP sender for invoice email. Reads SMTP configuration from
/// <see cref="IPlatformEmailDeliveryConfigResolver"/> — the same
/// platform_smtp_config row the SysAdmin verification sender uses, so
/// SysAdmin verification mail and Business invoice mail always share
/// one outbound configuration.
///
/// Sends multi-part email — text/plain alternate view + text/html
/// alternate view — with a single PDF attachment. Renders multiple
/// recipients from the operator-supplied To / Cc / Bcc.
/// </summary>
public sealed class SmtpInvoiceEmailSender : IInvoiceEmailSender
{
    private readonly IPlatformEmailDeliveryConfigResolver _configResolver;
    private readonly ILogger<SmtpInvoiceEmailSender> _logger;

    public SmtpInvoiceEmailSender(
        IPlatformEmailDeliveryConfigResolver configResolver,
        ILogger<SmtpInvoiceEmailSender> logger)
    {
        _configResolver = configResolver;
        _logger = logger;
    }

    public async Task<InvoiceEmailSendResult> SendAsync(
        InvoiceEmailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await _configResolver.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new InvoiceEmailSendResult(false,
                "SMTP is not configured yet. Configure it in SysAdmin → Operations → SMTP first.");
        }

        var configurationError = snapshot.GetConfigurationError();
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            return new InvoiceEmailSendResult(false, configurationError);
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(snapshot.FromEmail.Trim(), snapshot.FromDisplayName.Trim()),
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

        using var client = new SmtpClient(snapshot.Host.Trim(), snapshot.Port)
        {
            EnableSsl = snapshot.UseSsl,
            Credentials = new NetworkCredential(snapshot.Username.Trim(), snapshot.Password),
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
