using Citus.SysAdmin.Api.Control;
using Citus.Ui.Shared.Control;
using Microsoft.Extensions.Options;

namespace Citus.SysAdmin.Api.Tests;

public sealed class SysAdminControlStateTests
{
    [Fact]
    public void GetContext_UsesSystemScope_WhenNoCompaniesAreConfigured()
    {
        var state = CreateState(new SysAdminControlOptions());

        var context = state.GetContext();

        Assert.True(context.ActiveCompany.IsSystemScope);
        Assert.Equal("SYS", context.ActiveCompany.CompanyCode);
        Assert.Empty(context.AvailableCompanies);
        Assert.Empty(state.GetCompanies());
        Assert.Empty(state.GetUsers());
    }

    [Fact]
    public void GetContext_UsesConfiguredActiveCompany()
    {
        var companyId = CompanyId.FromOrdinal(1);
        var state = CreateState(
            new SysAdminControlOptions
            {
                DefaultActiveCompanyId = companyId,
                Companies =
                [
                    new CompanyWorkspaceOptions
                    {
                        Id = companyId,
                        CompanyCode = "ALPHA",
                        CompanyName = "Alpha Manufacturing",
                        BaseCurrencyCode = "USD",
                        Status = "active"
                    }
                ]
            });

        var context = state.GetContext();

        Assert.Equal((object?)companyId, (object?)context.ActiveCompany.CompanyId);
        Assert.Equal("ALPHA", context.ActiveCompany.CompanyCode);
        Assert.Equal("Alpha Manufacturing", context.ActiveCompany.CompanyName);
        Assert.False(context.ActiveCompany.IsSystemScope);
    }

    [Fact]
    public void TrySetActiveCompany_SwitchesCompanyWhenManaged()
    {
        var alphaId = CompanyId.FromOrdinal(1);
        var betaId = CompanyId.FromOrdinal(2);
        var state = CreateState(
            new SysAdminControlOptions
            {
                DefaultActiveCompanyId = alphaId,
                Companies =
                [
                    new CompanyWorkspaceOptions
                    {
                        Id = alphaId,
                        CompanyCode = "ALPHA",
                        CompanyName = "Alpha Manufacturing",
                        BaseCurrencyCode = "USD",
                        Status = "active"
                    },
                    new CompanyWorkspaceOptions
                    {
                        Id = betaId,
                        CompanyCode = "BETA",
                        CompanyName = "Beta Retail Group",
                        BaseCurrencyCode = "CAD",
                        Status = "active"
                    }
                ]
            });

        var switched = state.TrySetActiveCompany(betaId, out var context);

        Assert.True(switched);
        Assert.NotNull(context);
        Assert.Equal(betaId, context!.ActiveCompany.CompanyId);
        Assert.Equal("BETA", context.ActiveCompany.CompanyCode);
    }

    [Fact]
    public void UpdateMaintenance_ReplacesStateAndMessage()
    {
        var state = CreateState(new SysAdminControlOptions());

        var updated = state.UpdateMaintenance(
            new MaintenanceUpdateRequest
            {
                Enabled = true,
                Message = "Planned maintenance window in progress.",
                ScheduledUntilUtc = DateTimeOffset.Parse("2026-04-14T02:30:00Z")
            });

        Assert.True(updated.Enabled);
        Assert.Equal("Planned maintenance window in progress.", updated.Message);
        Assert.Equal(DateTimeOffset.Parse("2026-04-14T02:30:00Z"), updated.ScheduledUntilUtc);
    }

    private static SysAdminControlState CreateState(SysAdminControlOptions options) =>
        new(Options.Create(options));
}
