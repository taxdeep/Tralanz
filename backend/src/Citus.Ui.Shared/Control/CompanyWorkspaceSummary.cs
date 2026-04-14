namespace Citus.Ui.Shared.Control;

public sealed record class CompanyWorkspaceSummary
{
    public Guid Id { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool MultiCurrencyEnabled { get; init; }

    public string Status { get; init; } = string.Empty;

    public int MemberCount { get; init; }
}
