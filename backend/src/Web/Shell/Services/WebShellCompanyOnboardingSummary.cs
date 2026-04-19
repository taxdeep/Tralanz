namespace Web.Shell.Services;

public sealed record class WebShellCompanyOnboardingSummary
{
    public Guid CompanyId { get; init; }

    public string CompanyName { get; init; } = string.Empty;

    public string CompanyCode { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public string Industry { get; init; } = string.Empty;

    public string OwnerDisplayName { get; init; } = string.Empty;

    public string OwnerEmail { get; init; } = string.Empty;

    public string TemplateKey { get; init; } = string.Empty;

    public string TemplateVersion { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int AccountCodeLength { get; init; }

    public DateTimeOffset? FirstTimeSetupCompletedAtUtc { get; init; }

    public DateTimeOffset? FirstBusinessLoginAcknowledgedAtUtc { get; init; }

    public bool RequiresOnboarding => FirstTimeSetupCompletedAtUtc.HasValue && !FirstBusinessLoginAcknowledgedAtUtc.HasValue;

    public IReadOnlyList<string> StarterAccountCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ReservedFamilies { get; init; } = Array.Empty<string>();

    public int StarterAccountCount { get; init; }

    public bool HasPrimaryBook { get; init; }

    public string StarterBankAccountCode { get; init; } = string.Empty;

    public bool HasStarterBankAccount => !string.IsNullOrWhiteSpace(StarterBankAccountCode);

    public bool HasReceivableControlAccount { get; init; }

    public bool HasPayableControlAccount { get; init; }

    public int ActiveTaxCodeCount { get; init; }

    public bool HasActiveTaxCodes => ActiveTaxCodeCount > 0;

    public bool SupportsInventoryFoundation =>
        string.Equals(Industry, "trading", StringComparison.OrdinalIgnoreCase);

    public bool HasStarterChart =>
        StarterAccountCount > 0 &&
        HasPrimaryBook &&
        HasReceivableControlAccount &&
        HasPayableControlAccount;
}
