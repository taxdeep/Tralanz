using Modules.CompanyAccess.SessionContext;
using SharedKernel.CompanyAccess;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessSessionDirectoryTests
{
    private static readonly UserId UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid OtherCompanyId = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6");

    [Fact]
    public void TryResolve_ReturnsSummary_WhenUserBelongsToCompany()
    {
        var directory = CreateDirectory();

        var success = directory.TryResolve(
            new BusinessSessionContext
            {
                UserId = UserId,
                ActiveCompanyId = CompanyId
            },
            out var resolution,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(resolution);
        Assert.Equal("Alice Rowan", resolution.User.DisplayName);
        Assert.Equal("NORTHWIND", resolution.ActiveCompany.CompanyCode);
        Assert.Single(resolution.AvailableCompanies);
    }

    [Fact]
    public void TryResolve_RejectsCompanyOutsideUserMembership()
    {
        var directory = CreateDirectory();

        var success = directory.TryResolve(
            new BusinessSessionContext
            {
                UserId = UserId,
                ActiveCompanyId = OtherCompanyId
            },
            out var resolution,
            out var error);

        Assert.False(success);
        Assert.Null(resolution);
        Assert.Equal(
            "Business user '7bd0e908-cfe7-4f7b-8a0d-f19292e4186d' does not belong to company 'e56df08c-39ae-405b-8ed2-247b97d2f9f6'.",
            error);
    }

    [Fact]
    public async Task ResolveAsync_UsesPersistedCompanyAccessRoles_WhenAvailable()
    {
        var directory = CreateDirectory(
            new StubCompanySessionContextWorkflow(
                CreatePersistedSessionContext(
                    UserId,
                    CompanyId,
                    ["user", "company_book_governance"])));

        var result = await directory.ResolveAsync(
            new BusinessSessionContext
            {
                UserId = UserId,
                ActiveCompanyId = CompanyId
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.NotNull(result.Resolution);
        Assert.Equal(["company_book_governance", "user"], result.Resolution!.User.Roles);
        Assert.Equal("NORTHWIND", result.Resolution.ActiveCompany.CompanyCode);
    }

    [Fact]
    public async Task ResolveAsync_PreservesPersistedCompanyReadOnlyStatus()
    {
        var directory = CreateDirectory(
            new StubCompanySessionContextWorkflow(
                CreatePersistedSessionContext(
                    UserId,
                    CompanyId,
                    ["owner"],
                    status: "inactive")));

        var result = await directory.ResolveAsync(
            new BusinessSessionContext
            {
                UserId = UserId,
                ActiveCompanyId = CompanyId
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Resolution);
        Assert.Equal("inactive", result.Resolution!.ActiveCompany.Status);
        Assert.True(result.Resolution.ActiveCompany.IsReadOnly);
    }

    [Fact]
    public async Task ResolveAsync_RejectsPersistedContextWithoutRequestedCompany()
    {
        var directory = CreateDirectory(
            new StubCompanySessionContextWorkflow(
                CreatePersistedSessionContext(
                    UserId,
                    CompanyId,
                    ["owner"])));

        var result = await directory.ResolveAsync(
            new BusinessSessionContext
            {
                UserId = UserId,
                ActiveCompanyId = OtherCompanyId
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Resolution);
        Assert.Contains("does not belong to company", result.Error, StringComparison.Ordinal);
    }

    private static BusinessSessionDirectory CreateDirectory() =>
        new(Microsoft.Extensions.Options.Options.Create(CreateFixtureOptions()));

    private static BusinessSessionDirectory CreateDirectory(ICompanySessionContextWorkflow workflow) =>
        new(Microsoft.Extensions.Options.Options.Create(CreateFixtureOptions()), workflow);

    // Test-local fixture: the production directory no longer carries any
    // built-in companies / users, so the tests own the data they exercise.
    // Names are arbitrary but kept consistent with the assertions below.
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
                Id = OtherCompanyId,
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

    private static CompanyAccessSessionContext CreatePersistedSessionContext(
        UserId userId,
        CompanyId activeCompanyId,
        IReadOnlyList<string> roles,
        string status = "active") =>
        new()
        {
            User = new CompanyAccessUserSummary
            {
                Id = userId,
                DisplayName = "Persisted Owner",
                Email = "persisted.owner@example.test",
                Username = "persisted.owner",
                Roles = roles
            },
            ActiveCompany = CreateCompanySummary(activeCompanyId, status),
            AvailableCompanies = [CreateCompanySummary(activeCompanyId, status)]
        };

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
