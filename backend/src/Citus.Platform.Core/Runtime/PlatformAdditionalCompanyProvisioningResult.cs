namespace Citus.Platform.Core.Runtime;

/// <summary>
/// Result of <see cref="Abstractions.IPlatformAdditionalCompanyProvisioningRepository.ProvisionAsync"/>.
///
/// Mirrors <see cref="PlatformFirstCompanyProvisioningResult"/> shape — same
/// fields the Business shell wants to render in a "company created"
/// confirmation — minus the owner-email field (the caller already knows
/// their own email).
/// </summary>
public sealed record class PlatformAdditionalCompanyProvisioningResult
{
    public bool Succeeded { get; init; }

    public string FailureCode { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;

    public CompanyId CompanyId { get; init; }

    public string CompanyEntityNumber { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

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
