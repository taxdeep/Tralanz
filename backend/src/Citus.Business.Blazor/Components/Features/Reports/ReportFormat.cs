using Citus.Business.Blazor.Services;
using Citus.Ui.Shared.Reports;
using Microsoft.JSInterop;

namespace Citus.Business.Blazor.Components.Features.Reports;

/// <summary>
/// Shared, dependency-light formatting + download helpers used by the report
/// body components, so each report doesn't re-declare the same one-liners.
/// </summary>
internal static class ReportFormat
{
    public static string Amount(decimal amount) => amount.ToString("N2");

    public static string ChartAmount(object value) =>
        value is IConvertible convertible
            ? Convert.ToDecimal(convertible).ToString("N0")
            : value?.ToString() ?? string.Empty;

    public static string AgingBucket(string bucket) =>
        bucket switch
        {
            "current" => "Current",
            "1_30" => "1-30",
            "31_60" => "31-60",
            "61_90" => "61-90",
            "over_90" => "> 90",
            _ => bucket
        };

    public static string? DocumentHref(string sourceType, Guid documentId) =>
        AccountingDocumentReviewRouteCatalog.TryBuildReviewHref(sourceType, documentId, out var href)
            ? href
            : null;

    public static async Task SaveCsvAsync(
        IJSRuntime jsRuntime,
        ReportCsvDownload? file,
        string unavailableMessage,
        Action<string?> setError)
    {
        if (file is null)
        {
            setError(unavailableMessage);
            return;
        }

        await jsRuntime.InvokeVoidAsync(
            "citusDownloads.saveTextFile",
            file.FileName,
            file.Content,
            file.ContentType);
    }
}
