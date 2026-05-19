namespace Modules.Company.FeatureManagement;

/// <summary>
/// One row in the per-company module-flag list. <see cref="UpdatedAtUtc"/>
/// and <see cref="UpdatedByUserId"/> are null when the company has no
/// explicit row yet — the gate returns <see cref="Enabled"/> = <c>false</c>
/// in that case (catalog modules are off by default).
/// </summary>
public sealed record CompanyModuleFlagSummary(
    CompanyId CompanyId,
    string ModuleKey,
    string DisplayName,
    string Description,
    bool Enabled,
    DateTimeOffset? UpdatedAtUtc,
    UserId? UpdatedByUserId);
