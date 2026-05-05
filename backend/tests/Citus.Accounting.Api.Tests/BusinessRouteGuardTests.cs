using Citus.Platform.Core.Runtime;
using Microsoft.AspNetCore.Http;
using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessRouteGuardTests
{
    private static readonly UserId UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");
    private static readonly CompanyId CompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");

    [Fact]
    public void Evaluate_BlocksWriteRequests_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            Array.Empty<object?>(),
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            });

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
    }

    [Fact]
    public void Evaluate_AllowsReadRequests_WhenMaintenanceModeIsEnabled()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            });

        Assert.True(result.Allowed);
        Assert.NotNull(result.Session);
        Assert.NotNull(result.Resolution);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public void Evaluate_RequiresSessionHeaders()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            new HeaderDictionary(),
            Array.Empty<object?>(),
            maintenanceState: null);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Contains(BusinessSessionHeaders.UserId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RejectsUserCompanyMembershipMismatch()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            CreateHeaders(UserId, Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6")),
            Array.Empty<object?>(),
            maintenanceState: null);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("does not belong to company", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AssignsResolvedSession_WhenRequestIsValid()
    {
        var guard = CreateGuard();

        var result = guard.Evaluate(
            HttpMethods.Get,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            maintenanceState: null);

        Assert.True(result.Allowed);
        Assert.NotNull(result.Session);
        Assert.NotNull(result.Resolution);
        Assert.Equal(UserId, result.Session.UserId);
        Assert.Equal(CompanyId, result.Session.ActiveCompanyId);
        Assert.Equal(["owner", "reports"], result.Session.Roles);
        Assert.Equal("NORTHWIND", result.Resolution!.ActiveCompany.CompanyCode);
    }

    [Fact]
    public async Task EvaluateAsync_AssignsPersistedCompanyAccessRoles_WhenAvailable()
    {
        var guard = CreateGuard(
            new StubCompanySessionContextWorkflow(
                new CompanyAccessSessionContext
                {
                    User = new CompanyAccessUserSummary
                    {
                        Id = UserId,
                        DisplayName = "Persisted Owner",
                        Email = "persisted.owner@example.test",
                        Username = "persisted.owner",
                        Roles = ["user", "company_book_governance"]
                    },
                    ActiveCompany = CreateCompanySummary(CompanyId),
                    AvailableCompanies = [CreateCompanySummary(CompanyId)]
                }));

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            maintenanceState: null,
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.NotNull(result.Session);
        Assert.NotNull(result.Resolution);
        Assert.Equal(["company_book_governance", "user"], result.Session!.Roles);
        Assert.Equal("active", result.Resolution!.ActiveCompany.Status);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsReads_WhenPersistedCompanyIsReadOnly()
    {
        var guard = CreateGuard(
            new StubCompanySessionContextWorkflow(
                new CompanyAccessSessionContext
                {
                    User = new CompanyAccessUserSummary
                    {
                        Id = UserId,
                        DisplayName = "Persisted Owner",
                        Email = "persisted.owner@example.test",
                        Username = "persisted.owner",
                        Roles = ["owner"]
                    },
                    ActiveCompany = CreateCompanySummary(CompanyId, "inactive"),
                    AvailableCompanies = [CreateCompanySummary(CompanyId, "inactive")]
                }));

        var result = await guard.EvaluateAsync(
            HttpMethods.Get,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            maintenanceState: null,
            CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.NotNull(result.Session);
        Assert.NotNull(result.Resolution);
        Assert.Equal("inactive", result.Resolution!.ActiveCompany.Status);
        Assert.True(result.Resolution.ActiveCompany.IsReadOnly);
    }

    [Fact]
    public async Task EvaluateAsync_BlocksWrites_WhenPersistedCompanyIsReadOnly()
    {
        var guard = CreateGuard(
            new StubCompanySessionContextWorkflow(
                new CompanyAccessSessionContext
                {
                    User = new CompanyAccessUserSummary
                    {
                        Id = UserId,
                        DisplayName = "Persisted Owner",
                        Email = "persisted.owner@example.test",
                        Username = "persisted.owner",
                        Roles = ["owner"]
                    },
                    ActiveCompany = CreateCompanySummary(CompanyId, "inactive"),
                    AvailableCompanies = [CreateCompanySummary(CompanyId, "inactive")]
                }));

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            maintenanceState: null,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("read-only access only", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Resolution);
        Assert.True(result.Resolution!.ActiveCompany.IsReadOnly);
    }

    [Fact]
    public async Task EvaluateAsync_PrefersMaintenanceBlock_OverPersistedCompanyReadOnlyGate()
    {
        var guard = CreateGuard(
            new StubCompanySessionContextWorkflow(
                new CompanyAccessSessionContext
                {
                    User = new CompanyAccessUserSummary
                    {
                        Id = UserId,
                        DisplayName = "Persisted Owner",
                        Email = "persisted.owner@example.test",
                        Username = "persisted.owner",
                        Roles = ["owner"]
                    },
                    ActiveCompany = CreateCompanySummary(CompanyId, "inactive"),
                    AvailableCompanies = [CreateCompanySummary(CompanyId, "inactive")]
                }));

        var result = await guard.EvaluateAsync(
            HttpMethods.Post,
            CreateHeaders(UserId, CompanyId),
            [
                new GuardProbeRequest
                {
                    CompanyId = CompanyId,
                    UserId = UserId
                }
            ],
            new PlatformMaintenanceState
            {
                Enabled = true,
                Message = "Maintenance window in progress."
            },
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("Maintenance window in progress.", result.Message);
        Assert.Null(result.Session);
        Assert.Null(result.Resolution);
    }

    private static BusinessRouteGuard CreateGuard() =>
        new(
            new BusinessSessionRequestReader(),
            new BusinessRequestContractGuard(),
            new BusinessSessionDirectory(Microsoft.Extensions.Options.Options.Create(CreateFixtureOptions())));

    private static BusinessRouteGuard CreateGuard(ICompanySessionContextWorkflow workflow) =>
        new(
            new BusinessSessionRequestReader(),
            new BusinessRequestContractGuard(),
            new BusinessSessionDirectory(Microsoft.Extensions.Options.Options.Create(CreateFixtureOptions()), workflow));

    // Test-local fixture. Production directory no longer carries built-ins.
    private static BusinessSessionOptions CreateFixtureOptions() => new()
    {
        Companies =
        [
            new BusinessSessionCompanyOptions
            {
                Id = CompanyId,
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
        ],
        Users =
        [
            new BusinessSessionUserOptions
            {
                Id = UserId,
                DisplayName = "Alice Rowan",
                Email = "alice.rowan@northwind.example",
                Username = "alice.rowan",
                Roles = ["owner", "reports"],
                CompanyIds = [CompanyId]
            }
        ]
    };

    private static HeaderDictionary CreateHeaders(UserId userId, CompanyId companyId) =>
        new()
        {
            [BusinessSessionHeaders.UserId] = userId.ToString(),
            [BusinessSessionHeaders.ActiveCompanyId] = companyId.ToString()
        };

    public sealed class GuardProbeRequest
    {
        public CompanyId CompanyId { get; init; }

        public UserId UserId { get; init; }
    }

    private static CompanyAccessCompanySummary CreateCompanySummary(CompanyId companyId, string status = "active") =>
        new()
        {
            Id = companyId,
            CompanyCode = "NORTHWIND",
            CompanyName = "Northwind Studio Ltd.",
            BaseCurrencyCode = "USD",
            MultiCurrencyEnabled = true,
            Status = status,
            IsReadOnly = !string.Equals(status, "active", StringComparison.Ordinal)
        };

    private sealed class StubCompanySessionContextWorkflow(CompanyAccessSessionContext? context) : ICompanySessionContextWorkflow
    {
        public Task<CompanyAccessSessionContext?> GetAsync(
            UserId userId,
            CompanyId? preferredActiveCompanyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(context);
    }
}
