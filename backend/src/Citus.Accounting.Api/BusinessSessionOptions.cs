namespace Citus.Accounting.Api;

public sealed class BusinessSessionOptions
{
    public const string SectionName = "BusinessSession";

    public List<BusinessSessionCompanyOptions> Companies { get; set; } = [];

    public List<BusinessSessionUserOptions> Users { get; set; } = [];
}

public sealed class BusinessSessionCompanyOptions
{
    public Guid Id { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string BaseCurrencyCode { get; set; } = "USD";

    public bool MultiCurrencyEnabled { get; set; }
}

public sealed class BusinessSessionUserOptions
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = [];

    public List<Guid> CompanyIds { get; set; } = [];
}
