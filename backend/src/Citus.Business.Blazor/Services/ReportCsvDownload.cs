namespace Citus.Business.Blazor.Services;

public sealed record class ReportCsvDownload
{
    public string FileName { get; init; } = "report.csv";

    public string Content { get; init; } = string.Empty;

    public string ContentType { get; init; } = "text/csv; charset=utf-8";
}
