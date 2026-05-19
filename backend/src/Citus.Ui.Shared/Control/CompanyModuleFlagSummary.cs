namespace Citus.Ui.Shared.Control;

/// <summary>
/// Transport DTO for the SysAdmin and Business "module flags" endpoints.
/// Mirrors <c>Modules.Company.FeatureManagement.CompanyModuleFlagSummary</c>
/// — kept in <c>Citus.Ui.Shared</c> so Blazor hosts can deserialize
/// responses without dragging in the business module.
/// </summary>
public sealed record class CompanyModuleFlagSummary
{
    public CompanyId CompanyId { get; init; }

    public string ModuleKey { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public UserId? UpdatedByUserId { get; init; }
}
