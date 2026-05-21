namespace Citus.Accounting.Api;

/// <summary>
/// Business-side request body for
/// <c>PUT /accounting/company/module-flags/{moduleKey}</c>. Sibling of
/// the SysAdmin <c>CompanyModuleFlagUpdateRequest</c>; declared here so
/// the SysAdmin → Business projects don't cross-reference DTOs.
/// </summary>
public sealed record CompanyModuleFlagToggleHttpRequest(bool Enabled, string? Reason = null);
