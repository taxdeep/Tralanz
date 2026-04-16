namespace Citus.Ui.Shared.Business;

public sealed record class BusinessCompanySummary
{
    public Guid Id { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool MultiCurrencyEnabled { get; init; }

    public string Status { get; init; } = "active";

    public bool IsReadOnly { get; init; }
}
