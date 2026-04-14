using Citus.Ui.Shared.Business;
using Microsoft.Extensions.Options;
using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;
using Web.Shell.Configuration;

namespace Web.Shell.State;

public sealed class WebShellState
{
    private readonly ICompanySessionContextWorkflow _workflow;
    private readonly CompanyAccessSessionContext _fallbackContext;
    private bool _hydrationAttempted;

    public WebShellState(
        IOptions<WebShellAppHostOptions> options,
        ICompanySessionContextWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        var bootstrap = options.Value;
        _fallbackContext = BuildFallbackContext(bootstrap);
        ApplyContext(_fallbackContext);
    }

    public BusinessUserSummary User { get; private set; } = new();

    public BusinessCompanySummary ActiveCompany { get; private set; } = new();

    public IReadOnlyList<BusinessCompanySummary> AvailableCompanies { get; private set; } = Array.Empty<BusinessCompanySummary>();

    public Guid CurrentUserId => User.Id;

    public string ContextSource { get; private set; } = "app_host_fallback";

    public async Task EnsureHydratedAsync(CancellationToken cancellationToken = default)
    {
        if (_hydrationAttempted)
        {
            return;
        }

        _hydrationAttempted = true;
        var context = await _workflow.GetAsync(User.Id, ActiveCompany.Id, cancellationToken);
        if (context is null)
        {
            return;
        }

        ApplyContext(context);
        ContextSource = "company_access_membership";
    }

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

    private void ApplyContext(CompanyAccessSessionContext context)
    {
        User = new BusinessUserSummary
        {
            Id = context.User.Id,
            DisplayName = context.User.DisplayName,
            Email = context.User.Email,
            Username = context.User.Username,
            Roles = context.User.Roles.ToArray()
        };
        AvailableCompanies = context.AvailableCompanies
            .Select(
                static company => new BusinessCompanySummary
                {
                    Id = company.Id,
                    CompanyCode = company.CompanyCode,
                    CompanyName = company.CompanyName,
                    BaseCurrencyCode = company.BaseCurrencyCode,
                    MultiCurrencyEnabled = company.MultiCurrencyEnabled
                })
            .ToArray();
        ActiveCompany = AvailableCompanies.FirstOrDefault(company => company.Id == context.ActiveCompany.Id)
            ?? AvailableCompanies.First();
    }

    private static CompanyAccessSessionContext BuildFallbackContext(WebShellAppHostOptions bootstrap)
    {
        var companies = bootstrap.Companies
            .Where(company => company.Id != Guid.Empty)
            .Select(
                static company => new CompanyAccessCompanySummary
                {
                    Id = company.Id,
                    CompanyCode = company.CompanyCode.Trim().ToUpperInvariant(),
                    CompanyName = company.CompanyName.Trim(),
                    BaseCurrencyCode = company.BaseCurrencyCode.Trim().ToUpperInvariant(),
                    MultiCurrencyEnabled = company.MultiCurrencyEnabled
                })
            .OrderBy(company => company.CompanyCode, StringComparer.Ordinal)
            .ToArray();

        var activeCompany = ResolveInitialActiveCompany(bootstrap.DefaultActiveCompanyId, companies);
        return new CompanyAccessSessionContext
        {
            User = new CompanyAccessUserSummary
            {
                Id = bootstrap.BootstrapUserId,
                DisplayName = bootstrap.BootstrapUserDisplayName,
                Email = bootstrap.BootstrapUserEmail,
                Username = bootstrap.BootstrapUsername,
                Roles = bootstrap.BootstrapRoles
            },
            ActiveCompany = activeCompany,
            AvailableCompanies = companies
        };
    }

    private static CompanyAccessCompanySummary ResolveInitialActiveCompany(
        Guid? configuredCompanyId,
        IReadOnlyList<CompanyAccessCompanySummary> companies)
    {
        if (configuredCompanyId.HasValue)
        {
            var configured = companies.FirstOrDefault(company => company.Id == configuredCompanyId.Value);
            if (configured is not null)
            {
                return configured;
            }
        }

        return companies.FirstOrDefault()
            ?? new CompanyAccessCompanySummary
            {
                Id = Guid.Empty,
                CompanyCode = "UNCONFIGURED",
                CompanyName = "Unconfigured Company",
                BaseCurrencyCode = "USD",
                MultiCurrencyEnabled = false
            };
    }
}
