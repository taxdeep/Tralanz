namespace SharedKernel.CompanyAccess;

public sealed record class CompanyAccessCompanySummary
{
    public Guid Id { get; init; }

    public string CompanyCode { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public bool MultiCurrencyEnabled { get; init; }
}
