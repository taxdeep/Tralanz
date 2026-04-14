namespace SharedKernel.Reports;

public sealed record class ReportTypeOption
{
    public required ReportType Type { get; init; }

    public required string Code { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    public bool IsRecommended { get; init; }
}
