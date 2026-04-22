namespace Citus.Platform.Core.Runtime;

public sealed record class PlatformFirstCompanyProvisioningCommand
{
    public Guid? SysAdminAccountId { get; init; }

    public string OwnerDisplayName { get; init; } = string.Empty;

    public string OwnerEmail { get; init; } = string.Empty;

    public string OwnerPassword { get; init; } = string.Empty;

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
