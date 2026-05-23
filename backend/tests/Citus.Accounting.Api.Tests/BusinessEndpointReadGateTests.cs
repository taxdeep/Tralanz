using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessEndpointReadGateTests
{
    [Fact]
    public void ValidateCompanyScopedRead_BlocksMissingSession()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            session: null,
            CompanyId.FromOrdinal(1),
            "accounting",
            "view company books");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        Assert.Equal("business_session_required", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksCrossCompanyQuery()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "owner"),
            CompanyId.FromOrdinal(2),
            "accounting",
            "view company books");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("active_company_mismatch", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksSessionWithoutModuleAuthority()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "ar"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "view company books");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
        Assert.Contains("accounting", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedRead_AllowsMatchingCompanyAndAuthorizedModule()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "company_book_governance"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "view company books");

        Assert.True(result.Allowed);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("company_scoped_read_allowed", result.OutcomeCode);
        Assert.Null(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedRead_AllowsReportsUserToExportReports()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "reports"),
            CompanyId.FromOrdinal(1),
            "reports",
            "export trial balance");

        Assert.True(result.Allowed);
        Assert.Equal("company_scoped_read_allowed", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksArUserFromExportingReports()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "ar"),
            CompanyId.FromOrdinal(1),
            "reports",
            "export trial balance");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
        Assert.NotNull(result.Response);
    }

    [Fact]
    public void ValidateCompanyScopedRead_AllowsReportsUserToViewReportWidgets()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "reports"),
            CompanyId.FromOrdinal(1),
            "reports",
            "view sales cash flow");

        Assert.True(result.Allowed);
        Assert.Equal("company_scoped_read_allowed", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksSalesUserFromViewingReportWidgets()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "sales"),
            CompanyId.FromOrdinal(1),
            "reports",
            "view sales cash flow");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_AllowsArUserToViewArOpenItems()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "ar"),
            CompanyId.FromOrdinal(1),
            "ar_payments",
            "view AR open items");

        Assert.True(result.Allowed);
        Assert.Equal("company_scoped_read_allowed", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksArUserFromViewingApOpenItems()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "ar"),
            CompanyId.FromOrdinal(1),
            "ap_payments",
            "view AP open items");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_AllowsApUserToPreviewApAdjustments()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "ap"),
            CompanyId.FromOrdinal(1),
            "ap_payments",
            "preview AP open-item adjustments");

        Assert.True(result.Allowed);
        Assert.Equal("company_scoped_read_allowed", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksApUserFromPreviewingArAdjustments()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "ap"),
            CompanyId.FromOrdinal(1),
            "ar_payments",
            "preview AR open-item adjustments");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_AllowsAccountingGovernanceToViewSourceDocumentReversalPlans()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "company_book_governance"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "view source document reverse execution plans");

        Assert.True(result.Allowed);
        Assert.Equal("company_scoped_read_allowed", result.OutcomeCode);
    }

    [Fact]
    public void ValidateCompanyScopedRead_BlocksSalesUserFromSourceDocumentReversalPlans()
    {
        var result = BusinessEndpointReadGate.ValidateCompanyScopedRead(
            CreateSession(CompanyId.FromOrdinal(1), "sales"),
            CompanyId.FromOrdinal(1),
            "accounting",
            "view source document reverse execution plans");

        Assert.False(result.Allowed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("blocked_business_operation_authority", result.OutcomeCode);
    }

    private static BusinessSessionContext CreateSession(CompanyId companyId, params string[] roles) =>
        new()
        {
            UserId = UserId.FromOrdinal(1),
            ActiveCompanyId = companyId,
            Roles = roles
        };
}
