using Citus.Ui.Shared.Business;
using Microsoft.Extensions.Options;

namespace Citus.Accounting.Api;

public sealed class BusinessSessionDirectory
{
    private readonly IReadOnlyDictionary<Guid, BusinessSessionCompanyOptions> _companies;
    private readonly IReadOnlyDictionary<Guid, BusinessSessionUserOptions> _users;

    public BusinessSessionDirectory(IOptions<BusinessSessionOptions> options)
    {
        var companies = options.Value.Companies.Count > 0 ? options.Value.Companies : GetDefaultCompanies();
        var users = options.Value.Users.Count > 0 ? options.Value.Users : GetDefaultUsers();

        _companies = companies.ToDictionary(company => company.Id);
        _users = users.ToDictionary(user => user.Id);
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
            MultiCurrencyEnabled = company.MultiCurrencyEnabled
        };

    private static List<BusinessSessionCompanyOptions> GetDefaultCompanies() =>
    [
        new BusinessSessionCompanyOptions
        {
            Id = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc"),
            CompanyCode = "NORTHWIND",
            CompanyName = "Northwind Studio Ltd.",
            BaseCurrencyCode = "USD",
            MultiCurrencyEnabled = true
        },
        new BusinessSessionCompanyOptions
        {
            Id = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6"),
            CompanyCode = "BLUEHARBOR",
            CompanyName = "Blue Harbor Trading Co.",
            BaseCurrencyCode = "CAD",
            MultiCurrencyEnabled = false
        }
    ];

    private static List<BusinessSessionUserOptions> GetDefaultUsers() =>
    [
        new BusinessSessionUserOptions
        {
            Id = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
            DisplayName = "Alice Rowan",
            Email = "alice.rowan@northwind.example",
            Username = "alice.rowan",
            Roles = ["owner", "reports"],
            CompanyIds = [Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc")]
        },
        new BusinessSessionUserOptions
        {
            Id = Guid.Parse("3512739f-2af3-41f5-8fd4-d648d913a274"),
            DisplayName = "Ben Mercer",
            Email = "ben.mercer@blueharbor.example",
            Username = "ben.mercer",
            Roles = ["user", "ap"],
            CompanyIds = [Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6")]
        }
    ];
}
