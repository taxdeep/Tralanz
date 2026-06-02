namespace Modules.Company.FeatureManagement;

public sealed record CompanyModuleFlagAccessStatus(
    CompanyId CompanyId,
    string ModuleKey,
    bool Enabled,
    DateTimeOffset? AccessExpiresAtUtc,
    bool IsExpired)
{
    public bool AllowsRead => Enabled;

    public bool AllowsWrite => Enabled && !IsExpired;
}
