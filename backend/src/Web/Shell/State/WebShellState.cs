using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Shell;
using Microsoft.Extensions.Options;
using Web.Shell.Configuration;

namespace Web.Shell.State;

public sealed class WebShellState
{
    public WebShellState(IOptions<WebShellAppHostOptions> options)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        ClearAuthenticatedSession();
    }

    public BusinessUserSummary User { get; private set; } = new();

    public BusinessCompanySummary ActiveCompany { get; private set; } = new();

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; private set; } = Array.Empty<BusinessCompanySummary>();

    public MaintenanceStateSummary MaintenanceState { get; private set; } = new()
    {
        Enabled = false,
        Message = "Platform runtime is accepting interactive changes."
    };

    public string SessionToken { get; private set; } = string.Empty;

    public DateTimeOffset? SessionExpiresAtUtc { get; private set; }

    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(SessionToken) &&
        User.Id != Guid.Empty &&
        ActiveCompany.Id != Guid.Empty;

    public Guid CurrentUserId => User.Id;

    public string ContextSource { get; private set; } = "unauthenticated";

    public bool IsCompanyReadOnly => ActiveCompany.IsReadOnly;

    public bool AreWritesBlocked => !IsAuthenticated || MaintenanceState.Enabled || ActiveCompany.IsReadOnly;

    public string WriteBlockMessage =>
        !IsAuthenticated
            ? "Business sign-in is required before interactive work can continue."
            : MaintenanceState.Enabled
                ? MaintenanceState.Message
                : ActiveCompany.IsReadOnly
                    ? $"Company {ActiveCompany.CompanyName} is {NormalizeStatus(ActiveCompany.Status)} and currently read-only."
                    : "Business writes are available.";

    public Task EnsureHydratedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public bool TrySetActiveCompany(Guid companyId)
    {
        var company = AvailableCompanies.FirstOrDefault(candidate => candidate.Id == companyId);
        if (company is null)
        {
            return false;
        }

        ActiveCompany = company;
        return true;
    }

    public void ApplyAuthenticatedSession(
        string sessionToken,
        BusinessSessionContextSummary context,
        DateTimeOffset expiresAtUtc)
    {
        SessionToken = sessionToken.Trim();
        SessionExpiresAtUtc = expiresAtUtc;
        ApplyBusinessSessionContext(context);
    }

    public void ClearAuthenticatedSession()
    {
        SessionToken = string.Empty;
        SessionExpiresAtUtc = null;
        User = new BusinessUserSummary();
        ActiveCompany = new BusinessCompanySummary();
        AvailableCompanies = Array.Empty<BusinessCompanySummary>();
        MaintenanceState = new MaintenanceStateSummary
        {
            Enabled = false,
            Message = "Platform runtime is accepting interactive changes."
        };
        ContextSource = "unauthenticated";
    }

    public void ApplyBusinessSessionContext(BusinessSessionContextSummary context)
    {
        User = context.User;
        ActiveCompany = context.ActiveCompany;
        AvailableCompanies = context.AvailableCompanies;
        MaintenanceState = context.MaintenanceState;
        ContextSource = IsAuthenticated
            ? "business_session_api"
            : "transient_business_context";
    }

    private static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? "inactive"
            : status.Trim().ToLowerInvariant();
}
