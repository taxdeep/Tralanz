namespace Citus.Ui.Shared.Shell;

public sealed record class CompanyContextSummary
{
    public CompanyId? CompanyId { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public bool IsSystemScope { get; init; }
}
