namespace Citus.Accounting.Api.Endpoints.Support;

/// <summary>
/// Business sign-in/session endpoint helpers extracted verbatim from
/// Program.cs (P1, behavior-preserving): session-lifetime resolution,
/// session-token header reading, and the session-summary mapping used by the
/// /auth/* endpoints.
/// </summary>
public static class BusinessSessionEndpointHelpers
{
    public static TimeSpan ResolveBusinessSessionLifetime(IConfiguration configuration)
    {
        var hours = configuration.GetValue<int?>("BusinessAuthentication:SessionHours") ?? 12;
        if (hours <= 0) hours = 12;
        return TimeSpan.FromHours(hours);
    }

    public static string? ReadBusinessSessionToken(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue(
                Citus.Ui.Shared.Business.BusinessAuthHeaderNames.SessionToken,
                out var tokenValues))
        {
            return tokenValues.FirstOrDefault();
        }

        if (httpContext.Request.Headers.TryGetValue(
                Citus.Ui.Shared.Business.BusinessAuthHeaderNames.LegacySessionToken,
                out tokenValues))
        {
            return tokenValues.FirstOrDefault();
        }

        return null;
    }

    public static async Task<Citus.Ui.Shared.Business.BusinessAuthSessionSummary?> BuildBusinessSessionSummaryAsync(
        global::Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
        UserId userId,
        CompanyId activeCompanyId,
        CancellationToken cancellationToken)
    {
        var context = await contextWorkflow.GetAsync(userId, activeCompanyId, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var active = context.AvailableCompanies.FirstOrDefault(c => c.Id == activeCompanyId)
            ?? context.AvailableCompanies.FirstOrDefault();
        if (active is null)
        {
            return null;
        }

        return new Citus.Ui.Shared.Business.BusinessAuthSessionSummary
        {
            User = new Citus.Ui.Shared.Business.BusinessUserSummary
            {
                Id = context.User.Id,
                DisplayName = context.User.DisplayName,
                Email = context.User.Email,
                Username = context.User.Username,
                Roles = context.User.Roles.ToArray()
            },
            ActiveCompany = ToBusinessCompanySummary(active),
            AvailableCompanies = context.AvailableCompanies.Select(ToBusinessCompanySummary).ToArray()
        };
    }

    public static Citus.Ui.Shared.Business.BusinessCompanySummary ToBusinessCompanySummary(
        SharedKernel.CompanyAccess.CompanyAccessCompanySummary company) =>
        new()
        {
            Id = company.Id,
            CompanyCode = company.CompanyCode,
            CompanyName = company.CompanyName,
            BaseCurrencyCode = company.BaseCurrencyCode,
            MultiCurrencyEnabled = company.MultiCurrencyEnabled,
            InventoryModuleEnabled = company.InventoryModuleEnabled,
            Status = string.IsNullOrWhiteSpace(company.Status) ? "active" : company.Status,
            IsReadOnly = company.IsReadOnly,
            MoneyDecimals = company.MoneyDecimals
        };
}
