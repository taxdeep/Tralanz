namespace Citus.SysAdmin.Api.Control;

public sealed class SysAdminControlOptions
{
    public const string SectionName = "SysAdminControl";

    public SysAdminOperatorOptions Operator { get; set; } = new();

    public CompanyId? DefaultActiveCompanyId { get; set; }

    public MaintenanceOptions Maintenance { get; set; } = new();

    public List<CompanyWorkspaceOptions> Companies { get; set; } = [];

    public List<ManagedUserOptions> Users { get; set; } = [];
}

public sealed class SysAdminOperatorOptions
{
    public string DisplayName { get; set; } = "Platform Administrator";

    public string Email { get; set; } = "sysadmin@tralanz.local";

    public List<string> Roles { get; set; } = ["sysadmin"];
}

public sealed class MaintenanceOptions
{
    public bool Enabled { get; set; }

    public string Message { get; set; } = "Platform runtime is accepting interactive changes.";

    public DateTimeOffset? ScheduledUntilUtc { get; set; }
}

public sealed class CompanyWorkspaceOptions
{
    public CompanyId Id { get; set; } = CompanyId.FromOrdinal(1);

    public string CompanyCode { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string BaseCurrencyCode { get; set; } = "USD";

    public bool MultiCurrencyEnabled { get; set; }

    public string Status { get; set; } = "active";
}

public sealed class ManagedUserOptions
{
    public UserId Id { get; set; } = UserId.FromOrdinal(1);

    public string DisplayName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public bool IsSysAdmin { get; set; }

    public List<string> Roles { get; set; } = [];

    public List<CompanyId> CompanyIds { get; set; } = [];
}
