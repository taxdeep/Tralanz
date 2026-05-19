namespace Citus.SysAdmin.Api.Control;

/// <summary>
/// PUT body for /control/companies/{companyId}/module-flags/{moduleKey}.
/// Reason is required so every toggle leaves an honest audit row.
/// </summary>
public sealed record CompanyModuleFlagUpdateRequest(bool Enabled, string Reason);
