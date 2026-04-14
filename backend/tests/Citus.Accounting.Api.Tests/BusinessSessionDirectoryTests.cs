namespace Citus.Accounting.Api.Tests;

public sealed class BusinessSessionDirectoryTests
{
    [Fact]
    public void TryResolve_ReturnsSummary_WhenUserBelongsToCompany()
    {
        var directory = CreateDirectory();

        var success = directory.TryResolve(
            new BusinessSessionContext
            {
                UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
                ActiveCompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc")
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
                UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
                ActiveCompanyId = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6")
            },
            out var resolution,
            out var error);

        Assert.False(success);
        Assert.Null(resolution);
        Assert.Equal(
            "Business user '7bd0e908-cfe7-4f7b-8a0d-f19292e4186d' does not belong to company 'e56df08c-39ae-405b-8ed2-247b97d2f9f6'.",
            error);
    }

    private static BusinessSessionDirectory CreateDirectory() =>
        new(Microsoft.Extensions.Options.Options.Create(new BusinessSessionOptions()));
}
