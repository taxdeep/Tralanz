namespace Modules.Company.FeatureManagement;

public sealed record CompanyModuleFlagUpdateResult(
    CompanyModuleFlagSummary Flag,
    bool Changed,
    string Reason);
