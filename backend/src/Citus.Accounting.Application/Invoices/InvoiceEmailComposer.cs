using System.Net;

namespace Citus.Accounting.Application.Invoices;

/// <summary>
/// Pure function — turns an <see cref="InvoiceRenderModel"/> + an
/// optional operator-typed message into the subject / HTML body /
/// plain-text body the SMTP sender ships. No template lookup yet
/// (Batch 3 will plumb that through here).
/// </summary>
public static class InvoiceEmailComposer
{
    public static InvoiceEmailComposition Compose(
        InvoiceRenderModel model,
        string? operatorMessage,
        string? subjectTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var subject = ApplySubjectTemplate(
            subjectTemplate,
            model.Header.DisplayNumber,
            model.Issuer.CompanyName);
        var totalLine = $"{model.Totals.Total:N2} {model.Totals.CurrencyCode}";
        var dueLine = model.Header.DueDate is { } due
            ? due.ToString("yyyy-MM-dd")
            : "On receipt";
        var greeting = string.IsNullOrWhiteSpace(model.BillTo.DisplayName)
            ? "Hello,"
            : $"Hello {model.BillTo.DisplayName.Trim()},";
        var operatorBlock = string.IsNullOrWhiteSpace(operatorMessage)
            ? string.Empty
            : operatorMessage!.Trim();

        var htmlBody = BuildHtmlBody(
            greeting,
            model.Issuer.CompanyName,
            model.Header.DisplayNumber,
            totalLine,
            dueLine,
            operatorBlock,
            model.PaymentInstructions);

        var plainText = BuildPlainTextBody(
            greeting,
            model.Issuer.CompanyName,
            model.Header.DisplayNumber,
            totalLine,
            dueLine,
            operatorBlock,
            model.PaymentInstructions);

        return new InvoiceEmailComposition(subject, htmlBody, plainText);
    }

    private static string BuildHtmlBody(
        string greeting,
        string companyName,
        string invoiceNumber,
        string totalLine,
        string dueLine,
        string operatorBlock,
        string paymentInstructions)
    {
        var operatorHtml = string.IsNullOrWhiteSpace(operatorBlock)
            ? string.Empty
            : $"<p>{Encode(operatorBlock).Replace("\n", "<br/>")}</p>";

        var payHtml = string.IsNullOrWhiteSpace(paymentInstructions)
            ? string.Empty
            : $"""
              <p style="margin-top:16px;font-size:13px;color:#444;">
                <strong>Payment instructions:</strong><br/>
                {Encode(paymentInstructions).Replace("\n", "<br/>")}
              </p>
              """;

        return $"""
            <!DOCTYPE html>
            <html>
              <body style="font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;color:#1a1a1a;line-height:1.55;max-width:560px;">
                <p>{Encode(greeting)}</p>
                <p>
                  Please find attached invoice <strong>{Encode(invoiceNumber)}</strong>
                  from <strong>{Encode(companyName)}</strong>.
                </p>
                <table cellpadding="0" cellspacing="0" style="margin:16px 0;border-collapse:collapse;">
                  <tr>
                    <td style="padding:6px 14px 6px 0;color:#666;font-size:12px;text-transform:uppercase;letter-spacing:0.04em;">Total</td>
                    <td style="padding:6px 0;font-weight:700;">{Encode(totalLine)}</td>
                  </tr>
                  <tr>
                    <td style="padding:6px 14px 6px 0;color:#666;font-size:12px;text-transform:uppercase;letter-spacing:0.04em;">Due</td>
                    <td style="padding:6px 0;">{Encode(dueLine)}</td>
                  </tr>
                </table>
                {operatorHtml}
                {payHtml}
                <p style="margin-top:24px;color:#666;font-size:12px;">
                  Thank you for your business.<br/>
                  {Encode(companyName)}
                </p>
              </body>
            </html>
            """;
    }

    private static string BuildPlainTextBody(
        string greeting,
        string companyName,
        string invoiceNumber,
        string totalLine,
        string dueLine,
        string operatorBlock,
        string paymentInstructions)
    {
        var sections = new List<string>
        {
            greeting,
            $"Please find attached invoice {invoiceNumber} from {companyName}.",
            $"Total : {totalLine}",
            $"Due   : {dueLine}",
        };

        if (!string.IsNullOrWhiteSpace(operatorBlock))
        {
            sections.Add(operatorBlock);
        }

        if (!string.IsNullOrWhiteSpace(paymentInstructions))
        {
            sections.Add($"Payment instructions:{Environment.NewLine}{paymentInstructions}");
        }

        sections.Add($"Thank you for your business.{Environment.NewLine}{companyName}");

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            sections);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>
    /// Applies a {invoiceNumber} / {companyName} placeholder substitution
    /// against a subject template. Falls back to the canonical "Invoice N
    /// from C" form when the template is empty or null. Defensive against
    /// missing closing braces by treating an unrecognized token as
    /// literal text — never throws.
    /// </summary>
    private static string ApplySubjectTemplate(
        string? template,
        string invoiceNumber,
        string companyName)
    {
        var raw = string.IsNullOrWhiteSpace(template)
            ? "Invoice {invoiceNumber} from {companyName}"
            : template!.Trim();

        return raw
            .Replace("{invoiceNumber}", invoiceNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{companyName}", companyName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record InvoiceEmailComposition(
    string Subject,
    string HtmlBody,
    string PlainTextBody);
