namespace Citus.Platform.Core.Runtime;

/// <summary>
/// Input to <see cref="Abstractions.IPlatformAdditionalCompanyProvisioningRepository.ProvisionAsync"/>.
///
/// Strict subset of <see cref="PlatformFirstCompanyProvisioningCommand"/>:
/// the owner triplet (display name / email / password) is replaced by the
/// already-authenticated <see cref="OwnerUserId"/>, and the platform-side
/// SysAdmin actor field is dropped (this path is invoked from the
/// Business shell, not from a SysAdmin operator). All other fields keep
/// their original semantics so the shared chart-of-accounts seeding code
/// can read them without conditional plumbing.
/// </summary>
public sealed record class PlatformAdditionalCompanyProvisioningCommand
{
    public UserId OwnerUserId { get; init; }

    public string CompanyName { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public string Industry { get; init; } = string.Empty;

    public DateTime? IncorporatedOn { get; init; }

    public string FiscalYearEnd { get; init; } = string.Empty;

    public string BusinessNumber { get; init; } = string.Empty;

    public int AccountCodeLength { get; init; } = 4;

    public string Phone { get; init; } = string.Empty;

    public string CompanyEmail { get; init; } = string.Empty;

    public string AddressLine { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string ProvinceState { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    public string TemplateKey { get; init; } = string.Empty;

    public string BaseCurrencyCode { get; init; } = string.Empty;
}
