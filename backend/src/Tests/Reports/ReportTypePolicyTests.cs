using Modules.Reports.ReportType;
using SharedKernel.Reports;

namespace Tests.Reports;

public sealed class ReportTypePolicyTests
{
    [Fact]
    public void Default_Is_Accrual()
    {
        Assert.Equal(ReportType.Accrual, ReportTypeDefaults.Default);
    }

    [Theory]
    [InlineData("trial_balance")]
    [InlineData("income_statement")]
    [InlineData("balance_sheet")]
    [InlineData("ar_aging")]
    [InlineData("ap_aging")]
    public void KnownReports_Allow_All_Required_ReportTypes(string reportCode)
    {
        var options = ReportTypePolicy.GetAllowedOptions(reportCode);

        Assert.Collection(
            options,
            option => Assert.Equal(ReportType.Accrual, option.Type),
            option => Assert.Equal(ReportType.CashBasis, option.Type),
            option => Assert.Equal(ReportType.CashOnly, option.Type));
    }

    [Fact]
    public void UnknownReport_FallsBack_To_Accrual_Only()
    {
        var selection = ReportTypePolicy.Resolve("unknown_report", ReportType.CashBasis);

        Assert.Equal("unknown_report", selection.ReportCode);
        Assert.True(selection.WasAdjusted);
        Assert.Equal(ReportType.Accrual, selection.SelectedType);
        Assert.Single(selection.AllowedOptions);
        Assert.Equal(ReportType.Accrual, selection.AllowedOptions[0].Type);
    }

    [Fact]
    public void MissingRequestedType_Uses_Default_Without_Adjustment()
    {
        var selection = ReportTypePolicy.Resolve("trial_balance", null);

        Assert.False(selection.WasAdjusted);
        Assert.Equal(ReportType.Accrual, selection.SelectedType);
    }
}
