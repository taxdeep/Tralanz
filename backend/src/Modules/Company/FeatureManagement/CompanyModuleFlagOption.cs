namespace Modules.Company.FeatureManagement;

/// <summary>
/// Static metadata about a known toggleable module. Drives the
/// SysAdmin "Features" picker labels — no runtime decisions are
/// keyed off of this record.
/// </summary>
public sealed record CompanyModuleFlagOption(
    string Key,
    string DisplayName,
    string Description);
