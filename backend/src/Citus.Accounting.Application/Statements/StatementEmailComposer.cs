namespace Citus.Accounting.Application.Statements;

/// <summary>
/// Pure composer for the statement email subject + body. Mirrors
/// <c>InvoiceEmailComposer</c>. The rendered statement PDF is attached
/// separately by the endpoint; this only builds the covering message.
/// </summary>
public static class StatementEmailComposer
{
    public static StatementEmailComposition Compose(StatementRenderModel model, string? operatorMessage)
    {
        ArgumentNullException.ThrowIfNull(model);

        var subject = $"Statement from {model.Issuer.CompanyName} as of {model.AsOfDate:yyyy-MM-dd}";
        var balanceLine = $"{model.Totals.TotalOutstanding:N2} {model.BaseCurrencyCode}";
        var greeting = string.IsNullOrWhiteSpace(model.Party.DisplayName)
            ? "Hello,"
            : $"Hello {model.Party.DisplayName.Trim()},";
        var itemCount = model.Lines.Count;
        var itemLine = itemCount == 1 ? "1 open item" : $"{itemCount} open items";
        var operatorBlock = string.IsNullOrWhiteSpace(operatorMessage) ? null : operatorMessage!.Trim();

        var html = BuildHtml(model, greeting, balanceLine, itemLine, operatorBlock);
        var plain = BuildPlain(model, greeting, balanceLine, itemLine, operatorBlock);

        return new StatementEmailComposition(subject, html, plain);
    }

    private static string BuildHtml(
        StatementRenderModel model, string greeting, string balanceLine, string itemLine, string? operatorBlock)
    {
        var operatorHtml = operatorBlock is null
            ? string.Empty
            : $"<p style=\"margin:0 0 16px\">{WebEncode(operatorBlock).Replace("\n", "<br/>")}</p>";

        return $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1f2937;font-size:14px;line-height:1.5">
              <p style="margin:0 0 16px">{WebEncode(greeting)}</p>
              {operatorHtml}
              <p style="margin:0 0 8px">Please find attached your account statement from
                <strong>{WebEncode(model.Issuer.CompanyName)}</strong> as of
                <strong>{model.AsOfDate:yyyy-MM-dd}</strong>.</p>
              <p style="margin:0 0 16px">Open balance: <strong>{WebEncode(balanceLine)}</strong> ({WebEncode(itemLine)}).</p>
              <p style="margin:0;color:#6b7280;font-size:12px">The detailed statement is attached as a PDF.</p>
            </div>
            """;
    }

    private static string BuildPlain(
        StatementRenderModel model, string greeting, string balanceLine, string itemLine, string? operatorBlock)
    {
        var lines = new List<string> { greeting, string.Empty };
        if (operatorBlock is not null)
        {
            lines.Add(operatorBlock);
            lines.Add(string.Empty);
        }

        lines.Add($"Please find attached your account statement from {model.Issuer.CompanyName} as of {model.AsOfDate:yyyy-MM-dd}.");
        lines.Add($"Open balance: {balanceLine} ({itemLine}).");
        lines.Add(string.Empty);
        lines.Add("The detailed statement is attached as a PDF.");

        return string.Join("\n", lines);
    }

    private static string WebEncode(string raw) => System.Net.WebUtility.HtmlEncode(raw);
}

public sealed record StatementEmailComposition(
    string Subject,
    string HtmlBody,
    string PlainTextBody);
