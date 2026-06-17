namespace Citus.Accounting.Api;

/// <summary>
/// Body for PUT /accounting/company/settings/money-decimals. Self-contained DTO
/// (the SysAdmin → Business projects don't cross-reference DTOs). Allowed
/// values are 2 or 3; the endpoint and store both validate.
/// </summary>
public sealed record CompanyMoneyDecimalsHttpRequest(int MoneyDecimals);
