using Modules.Company.FeatureManagement;

namespace Citus.SysAdmin.Api.Tests;

public class CompanyModuleFlagCatalogTests
{
    [Fact]
    public void Options_includes_task()
    {
        Assert.Contains(CompanyModuleFlagCatalog.Options, o => o.Key == "task");
    }

    [Fact]
    public void NormalizeKey_accepts_known_key()
    {
        var normalized = CompanyModuleFlagCatalog.NormalizeKey("task");
        Assert.Equal("task", normalized);
    }

    [Fact]
    public void NormalizeKey_trims_and_lowercases()
    {
        var normalized = CompanyModuleFlagCatalog.NormalizeKey("  TASK  ");
        Assert.Equal("task", normalized);
    }

    [Fact]
    public void NormalizeKey_rejects_unknown_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CompanyModuleFlagCatalog.NormalizeKey("payroll"));
        Assert.Contains("payroll", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeKey_rejects_blank_input(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => CompanyModuleFlagCatalog.NormalizeKey(value!));
    }

    [Theory]
    [InlineData("task", true)]
    [InlineData("TASK", true)]
    [InlineData("payroll", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnown_matches_expected(string? value, bool expected)
    {
        Assert.Equal(expected, CompanyModuleFlagCatalog.IsKnown(value!));
    }
}
