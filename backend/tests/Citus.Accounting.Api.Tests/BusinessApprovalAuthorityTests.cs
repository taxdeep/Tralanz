namespace Citus.Accounting.Api.Tests;

public sealed class BusinessApprovalAuthorityTests
{
    [Fact]
    public void CanApproveOpenItemAdjustment_AllowsOwnerRole()
    {
        var session = CreateSession("owner");

        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentApproval(
            session,
            "AR",
            "approve");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanApproveOpenItemAdjustment_AllowsBookGovernanceRole()
    {
        var session = CreateSession("company_book_governance");

        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentApproval(
            session,
            "AP",
            "reject");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanApproveOpenItemAdjustment_BlocksOrdinaryUserRole()
    {
        var session = CreateSession("user", "ap");

        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentApproval(
            session,
            "AP",
            "approve");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_approval_authority", decision.OutcomeCode);
        Assert.Contains("owner or book-governance", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanManageOpenItemAdjustmentAccountMapping_BlocksOrdinaryUserRole()
    {
        var session = CreateSession("user", "settings");

        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentAccountMappingManagement(
            session,
            "save");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_mapping_management_authority", decision.OutcomeCode);
        Assert.Contains("owner or book-governance", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanManageOpenItemAdjustmentAccountMapping_AllowsOwnerRole()
    {
        var session = CreateSession("owner");

        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentAccountMappingManagement(
            session,
            "deactivate");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    private static BusinessSessionContext CreateSession(params string[] roles) =>
        new()
        {
            UserId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"),
            ActiveCompanyId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc"),
            Roles = roles
        };
}
