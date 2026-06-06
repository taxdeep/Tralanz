using System.Text;
using Citus.Ui.Shared.Reports;

namespace Citus.Accounting.Api;

internal static class AccountingEndpointHelpers
{
    public static IResult AccountingOperationBadRequest(InvalidOperationException exception)
    {
        var code = ResolveAccountingOperationErrorCode(exception.Message);
        return Results.BadRequest(new
        {
            code,
            message = exception.Message
        });
    }

    /// <summary>
    /// H3: resolves the idempotency key from the `Idempotency-Key` HTTP
    /// header first, falling back to the request body's IdempotencyKey
    /// field for transitional compatibility. The header is the long-term
    /// home (aligned with the P0-5 inventory POST pattern and with the
    /// HTTP idempotency draft RFC) — the body field stays accepted for
    /// one release while clients migrate, then becomes deprecated.
    ///
    /// When both are present the header wins; the body field is ignored.
    /// When neither is present the call falls back to the downstream
    /// handler's default key derivation (typically
    /// `"&lt;source-type&gt;:&lt;companyId&gt;:&lt;documentId&gt;"`).
    /// </summary>
    public static string? ResolveIdempotencyKey(HttpContext httpContext, string? bodyFallback)
    {
        if (httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var headerValues)
            && headerValues.Count > 0)
        {
            var headerKey = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(headerKey))
            {
                return headerKey.Trim();
            }
        }

        return bodyFallback;
    }

    public static string ResolveAccountingOperationErrorCode(string message)
    {
        if (message.Contains("locked by", StringComparison.OrdinalIgnoreCase)
            && message.Contains("through", StringComparison.OrdinalIgnoreCase))
        {
            return "posting_period_closed";
        }

        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "not_found";
        }

        if (message.Contains("Only draft", StringComparison.OrdinalIgnoreCase))
        {
            return "invalid_document_status";
        }

        return "invalid_operation";
    }

    public static IResult ToCsvFileResult(ReportCsvExporter.ReportCsvFile file) =>
        Results.File(Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);

    public static IReadOnlyList<string> SplitEmailList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('@', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
