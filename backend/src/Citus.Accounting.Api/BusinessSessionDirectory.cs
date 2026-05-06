using Citus.Ui.Shared.Business;
using Microsoft.Extensions.Options;
using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;

namespace Citus.Accounting.Api;

public sealed class BusinessSessionDirectory
{
    private readonly IReadOnlyDictionary<CompanyId, BusinessSessionCompanyOptions> _companies;
    private readonly IReadOnlyDictionary<UserId, BusinessSessionUserOptions> _users;
    private readonly ICompanySessionContextWorkflow? _companySessionContextWorkflow;
    private readonly bool _allowStaticFallback;

    public BusinessSessionDirectory(
        IOptions<BusinessSessionOptions> options,
        ICompanySessionContextWorkflow? companySessionContextWorkflow = null)
    {
        var hasConfiguredDirectory = options.Value.Companies.Count > 0 || options.Value.Users.Count > 0;

        _companies = options.Value.Companies.ToDictionary(company => company.Id);
        _users = options.Value.Users.ToDictionary(user => user.Id);
        _companySessionContextWorkflow = companySessionContextWorkflow;
        _allowStaticFallback = companySessionContextWorkflow is null || hasConfiguredDirectory;
    }

    public async Task<ResolveResult> ResolveAsync(
        BusinessSessionContext session,
        CancellationToken cancellationToken)
    {
        if (_companySessionContextWorkflow is not null)
        {
            CompanyAccessSessionContext? persistedContext;
            try
            {
                persistedContext = await _companySessionContextWorkflow.GetAsync(
                    session.UserId,
                    session.ActiveCompanyId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return ResolveResult.Rejected(
                    $"Business session could not be resolved from persisted company access membership. {ex.Message}");
            }

            if (persistedContext is not null)
            {
                var activeCompany = persistedContext.AvailableCompanies
                    .FirstOrDefault(company => company.Id == session.ActiveCompanyId);

                if (activeCompany is null)
                {
                    return ResolveResult.Rejected(
                        $"Business user '{session.UserId}' does not belong to company '{session.ActiveCompanyId}'.");
                }

                return ResolveResult.Accepted(
                    new BusinessSessionResolution
                    {
                        User = ToBusinessUserSummary(persistedContext.User),
                        ActiveCompany = ToBusinessCompanySummary(activeCompany),
                        AvailableCompanies = persistedContext.AvailableCompanies
                            .Select(ToBusinessCompanySummary)
                            .OrderBy(company => company.CompanyCode, StringComparer.Ordinal)
                            .ToArray()
                    });
            }

            // Persisted workflow returned no row for (UserId, ActiveCompanyId).
            // The session may still be a recognised bootstrap / dev-config user
            // whose IDs live in the in-memory directory but were never seeded
            // into the deployed company-access tables. Probe the static
            // directory; if it has an exact match, accept it. Otherwise
            // reject with the persisted-membership message — that wording is
            // still accurate, since the request hit the workflow first.
            if (TryResolve(session, out var staticResolution, out _))
            {
                return ResolveResult.Accepted(staticResolution!);
            }

            if (!_allowStaticFallback)
            {
                return ResolveResult.Rejected(
                    $"Business user '{session.UserId}' does not have an active persisted membership for company '{session.ActiveCompanyId}'.");
            }
        }

        return TryResolve(session, out var resolution, out var error)
            ? ResolveResult.Accepted(resolution!)
            : ResolveResult.Rejected(error ?? "The business session is not authorized for the requested company context.");
    }

    public bool TryResolve(
        BusinessSessionContext session,
        out BusinessSessionResolution? resolution,
        out string? error)
    {
        resolution = null;
        error = null;

        if (!_users.TryGetValue(session.UserId, out var user))
        {
            error = $"Business user '{session.UserId}' is not configured for the current environment.";
            return false;
        }

        if (!_companies.TryGetValue(session.ActiveCompanyId, out var activeCompany))
        {
            error = $"Business company '{session.ActiveCompanyId}' is not configured for the current environment.";
            return false;
        }

        if (!user.CompanyIds.Contains(session.ActiveCompanyId))
        {
            error = $"Business user '{session.UserId}' does not belong to company '{session.ActiveCompanyId}'.";
            return false;
        }

        var availableCompanies = user.CompanyIds
            .Distinct()
            .Where(_companies.ContainsKey)
            .Select(companyId => ToSummary(_companies[companyId]))
            .OrderBy(company => company.CompanyCode, StringComparer.Ordinal)
            .ToArray();

        resolution = new BusinessSessionResolution
        {
            User = ToSummary(user),
            ActiveCompany = ToSummary(activeCompany),
            AvailableCompanies = availableCompanies
        };

        return true;
    }

    private static BusinessUserSummary ToSummary(BusinessSessionUserOptions user) =>
        new()
        {
            Id = user.Id,
            DisplayName = user.DisplayName.Trim(),
            Email = user.Email.Trim(),
            Username = user.Username.Trim(),
            Roles = user.Roles
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Select(static role => role.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static role => role, StringComparer.Ordinal)
                .ToArray()
        };

    private static BusinessCompanySummary ToSummary(BusinessSessionCompanyOptions company) =>
        new()
        {
            Id = company.Id,
            CompanyCode = company.CompanyCode.Trim().ToUpperInvariant(),
            CompanyName = company.CompanyName.Trim(),
            BaseCurrencyCode = company.BaseCurrencyCode.Trim().ToUpperInvariant(),
            MultiCurrencyEnabled = company.MultiCurrencyEnabled,
            Status = "active",
            IsReadOnly = false
        };

    private static BusinessUserSummary ToBusinessUserSummary(CompanyAccessUserSummary user) =>
        new()
        {
            Id = user.Id,
            DisplayName = user.DisplayName.Trim(),
            Email = user.Email.Trim(),
            Username = user.Username.Trim(),
            Roles = user.Roles
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Select(static role => role.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static role => role, StringComparer.Ordinal)
                .ToArray()
        };

    private static BusinessCompanySummary ToBusinessCompanySummary(CompanyAccessCompanySummary company) =>
        new()
        {
            Id = company.Id,
            CompanyCode = company.CompanyCode.Trim().ToUpperInvariant(),
            CompanyName = company.CompanyName.Trim(),
            BaseCurrencyCode = company.BaseCurrencyCode.Trim().ToUpperInvariant(),
            MultiCurrencyEnabled = company.MultiCurrencyEnabled,
            InventoryModuleEnabled = company.InventoryModuleEnabled,
            Status = NormalizeCompanyStatus(company.Status),
            IsReadOnly = company.IsReadOnly ||
                !string.Equals(NormalizeCompanyStatus(company.Status), "active", StringComparison.Ordinal)
        };

    private static string NormalizeCompanyStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? "active"
            : status.Trim().ToLowerInvariant();

    public sealed record ResolveResult(
        bool Success,
        BusinessSessionResolution? Resolution,
        string? Error)
    {
        public static ResolveResult Accepted(BusinessSessionResolution resolution) =>
            new(true, resolution, null);

        public static ResolveResult Rejected(string error) =>
            new(false, null, error);
    }
}
