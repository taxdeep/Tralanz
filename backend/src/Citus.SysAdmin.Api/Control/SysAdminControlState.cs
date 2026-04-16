using Citus.Ui.Shared.Control;
using Citus.Ui.Shared.Shell;
using Microsoft.Extensions.Options;

namespace Citus.SysAdmin.Api.Control;

public sealed class SysAdminControlState
{
    private readonly object _sync = new();
    private readonly SysAdminOperatorSummary _operator;
    private readonly IReadOnlyList<CompanyWorkspaceSummary> _companies;
    private readonly IReadOnlyList<ManagedUserSummary> _users;
    private Guid? _activeCompanyId;
    private MaintenanceStateSummary _maintenanceState;

    public SysAdminControlState(IOptions<SysAdminControlOptions> options)
    {
        var value = options.Value;
        _operator = new SysAdminOperatorSummary
        {
            DisplayName = value.Operator.DisplayName,
            Email = value.Operator.Email,
            Roles = value.Operator.Roles
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Select(static role => role.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        _companies = BuildCompanies(value);
        _users = BuildUsers(value, _companies);
        _activeCompanyId = ResolveDefaultActiveCompanyId(value.DefaultActiveCompanyId, _companies);
        _maintenanceState = new MaintenanceStateSummary
        {
            Enabled = value.Maintenance.Enabled,
            Message = string.IsNullOrWhiteSpace(value.Maintenance.Message)
                ? "Platform runtime is accepting interactive changes."
                : value.Maintenance.Message.Trim(),
            ScheduledUntilUtc = value.Maintenance.ScheduledUntilUtc
        };
    }

    public SysAdminControlContextSummary GetContext() => GetContext(operatorOverride: null);

    public SysAdminControlContextSummary GetContext(SysAdminOperatorSummary? operatorOverride)
    {
        lock (_sync)
        {
            return BuildContext(operatorOverride);
        }
    }

    public IReadOnlyList<CompanyWorkspaceSummary> GetCompanies()
    {
        lock (_sync)
        {
            return _companies;
        }
    }

    public IReadOnlyList<ManagedUserSummary> GetUsers()
    {
        lock (_sync)
        {
            return _users;
        }
    }

    public MaintenanceStateSummary GetMaintenanceState()
    {
        lock (_sync)
        {
            return _maintenanceState;
        }
    }

    public bool TrySetActiveCompany(Guid companyId, out SysAdminControlContextSummary? context) =>
        TrySetActiveCompany(companyId, operatorOverride: null, out context);

    public bool TrySetActiveCompany(
        Guid companyId,
        SysAdminOperatorSummary? operatorOverride,
        out SysAdminControlContextSummary? context)
    {
        lock (_sync)
        {
            if (_companies.All(company => company.Id != companyId))
            {
                context = null;
                return false;
            }

            _activeCompanyId = companyId;
            context = BuildContext(operatorOverride);

            return true;
        }
    }

    public MaintenanceStateSummary UpdateMaintenance(MaintenanceUpdateRequest request)
    {
        lock (_sync)
        {
            _maintenanceState = new MaintenanceStateSummary
            {
                Enabled = request.Enabled,
                Message = string.IsNullOrWhiteSpace(request.Message)
                    ? "Maintenance state updated by SysAdmin."
                    : request.Message.Trim(),
                ScheduledUntilUtc = request.ScheduledUntilUtc
            };

            return _maintenanceState;
        }
    }

    public void SetMaintenanceState(MaintenanceStateSummary state)
    {
        lock (_sync)
        {
            _maintenanceState = new MaintenanceStateSummary
            {
                Enabled = state.Enabled,
                Message = string.IsNullOrWhiteSpace(state.Message)
                    ? "Maintenance state updated by SysAdmin."
                    : state.Message.Trim(),
                ScheduledUntilUtc = state.ScheduledUntilUtc
            };
        }
    }

    private SysAdminControlContextSummary BuildContext(SysAdminOperatorSummary? operatorOverride) =>
        new()
        {
            Operator = operatorOverride ?? _operator,
            ActiveCompany = ResolveActiveCompanyContext(),
            MaintenanceState = _maintenanceState,
            AvailableCompanies = _companies
        };

    private CompanyContextSummary ResolveActiveCompanyContext()
    {
        var activeCompany = _companies.FirstOrDefault(company => company.Id == _activeCompanyId);

        if (activeCompany is null)
        {
            return new CompanyContextSummary
            {
                CompanyCode = "SYS",
                CompanyName = "Platform Control",
                IsSystemScope = true
            };
        }

        return new CompanyContextSummary
        {
            CompanyId = activeCompany.Id,
            CompanyCode = activeCompany.CompanyCode,
            CompanyName = activeCompany.CompanyName,
            IsSystemScope = false
        };
    }

    private static Guid? ResolveDefaultActiveCompanyId(Guid? configuredCompanyId, IReadOnlyList<CompanyWorkspaceSummary> companies)
    {
        if (configuredCompanyId.HasValue && companies.Any(company => company.Id == configuredCompanyId.Value))
        {
            return configuredCompanyId.Value;
        }

        return companies.FirstOrDefault()?.Id;
    }

    private static IReadOnlyList<CompanyWorkspaceSummary> BuildCompanies(SysAdminControlOptions options)
    {
        var configuredCompanies = options.Companies.Count > 0
            ? options.Companies
            : GetDefaultCompanies();

        var userCounts = configuredCompanies.ToDictionary(company => company.Id, company => 0);

        foreach (var user in options.Users)
        {
            foreach (var companyId in user.CompanyIds.Distinct())
            {
                if (userCounts.ContainsKey(companyId))
                {
                    userCounts[companyId]++;
                }
            }
        }

        return configuredCompanies
            .Select(company => new CompanyWorkspaceSummary
            {
                Id = company.Id,
                CompanyCode = company.CompanyCode.Trim().ToUpperInvariant(),
                CompanyName = company.CompanyName.Trim(),
                BaseCurrencyCode = company.BaseCurrencyCode.Trim().ToUpperInvariant(),
                MultiCurrencyEnabled = company.MultiCurrencyEnabled,
                Status = string.IsNullOrWhiteSpace(company.Status) ? "active" : company.Status.Trim().ToLowerInvariant(),
                MemberCount = userCounts.TryGetValue(company.Id, out var count) ? count : 0
            })
            .OrderBy(company => company.CompanyCode, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ManagedUserSummary> BuildUsers(
        SysAdminControlOptions options,
        IReadOnlyList<CompanyWorkspaceSummary> companies)
    {
        var companyLookup = companies.ToDictionary(company => company.Id, company => company.CompanyCode);
        var configuredUsers = options.Users.Count > 0 ? options.Users : GetDefaultUsers(companies);

        return configuredUsers
            .Select(user => new ManagedUserSummary
            {
                Id = user.Id,
                DisplayName = user.DisplayName.Trim(),
                Email = user.Email.Trim(),
                Username = user.Username.Trim(),
                IsActive = user.IsActive,
                IsSysAdmin = user.IsSysAdmin,
                Roles = user.Roles
                    .Where(static role => !string.IsNullOrWhiteSpace(role))
                    .Select(static role => role.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static role => role, StringComparer.Ordinal)
                    .ToArray(),
                CompanyCodes = user.CompanyIds
                    .Distinct()
                    .Where(companyLookup.ContainsKey)
                    .Select(companyId => companyLookup[companyId])
                    .OrderBy(static code => code, StringComparer.Ordinal)
                    .ToArray()
            })
            .OrderBy(user => user.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static List<CompanyWorkspaceOptions> GetDefaultCompanies() =>
    [
        new CompanyWorkspaceOptions
        {
            Id = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc"),
            CompanyCode = "NORTHWIND",
            CompanyName = "Northwind Studio Ltd.",
            BaseCurrencyCode = "USD",
            MultiCurrencyEnabled = true,
            Status = "active"
        },
        new CompanyWorkspaceOptions
        {
            Id = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6"),
            CompanyCode = "BLUEHARBOR",
            CompanyName = "Blue Harbor Trading Co.",
            BaseCurrencyCode = "CAD",
            MultiCurrencyEnabled = false,
            Status = "active"
        }
    ];

    private static List<ManagedUserOptions> GetDefaultUsers(IReadOnlyList<CompanyWorkspaceSummary> companies)
    {
        var northwind = companies.FirstOrDefault(company => company.CompanyCode == "NORTHWIND")?.Id ?? Guid.Empty;
        var blueHarbor = companies.FirstOrDefault(company => company.CompanyCode == "BLUEHARBOR")?.Id ?? Guid.Empty;

        return
        [
            new ManagedUserOptions
            {
                Id = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
                DisplayName = "Alice Rowan",
                Email = "alice.rowan@northwind.example",
                Username = "alice.rowan",
                IsActive = true,
                Roles = ["owner", "reports"],
                CompanyIds = northwind == Guid.Empty ? [] : [northwind]
            },
            new ManagedUserOptions
            {
                Id = Guid.Parse("3512739f-2af3-41f5-8fd4-d648d913a274"),
                DisplayName = "Ben Mercer",
                Email = "ben.mercer@blueharbor.example",
                Username = "ben.mercer",
                IsActive = true,
                Roles = ["user", "ap"],
                CompanyIds = blueHarbor == Guid.Empty ? [] : [blueHarbor]
            },
            new ManagedUserOptions
            {
                Id = Guid.Parse("64f5186b-b854-49ec-a473-2f14554ecf77"),
                DisplayName = "Casey Lin",
                Email = "casey.lin@group.example",
                Username = "casey.lin",
                IsActive = true,
                IsSysAdmin = true,
                Roles = ["sysadmin"],
                CompanyIds = northwind == Guid.Empty || blueHarbor == Guid.Empty ? [] : [northwind, blueHarbor]
            }
        ];
    }
}
