namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformFirstCompanyProvisioningResult
{
    public bool Succeeded { get; init; }

    public string FailureCode { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;

    public CompanyId CompanyId { get; init; }

    public string CompanyEntityNumber { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public UserId OwnerUserId { get; init; }

    public string OwnerEmail { get; init; } = string.Empty;

    public Guid? CompanyBookId { get; init; }

    public string CompanyBookCode { get; init; } = string.Empty;

    public string TemplateKey { get; init; } = string.Empty;

    public string TemplateVersion { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;

    public int AccountCodeLength { get; init; }

    public IReadOnlyList<string> StarterAccountCodes { get; init; } = [];

    public IReadOnlyList<string> ReservedFamilies { get; init; } = [];

    public DateTimeOffset ProvisionedAtUtc { get; init; }
}
