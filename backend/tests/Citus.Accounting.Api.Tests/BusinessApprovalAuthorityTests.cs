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

    [Fact]
    public void CanManageGrIrClearingAccountPolicy_AllowsAccountingSettingsRole()
    {
        var session = CreateSession("company_accounting_settings");

        var decision = BusinessApprovalAuthority.EvaluateGrIrClearingAccountPolicyManagement(
            session,
            "save");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanManageGrIrClearingAccountPolicy_BlocksOrdinaryUserRole()
    {
        var session = CreateSession("user", "ap");

        var decision = BusinessApprovalAuthority.EvaluateGrIrClearingAccountPolicyManagement(
            session,
            "save");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_grir_policy_management_authority", decision.OutcomeCode);
        Assert.Contains("accounting-governance", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanExecuteGrIrSettlement_AllowsAccountingSettingsRole()
    {
        var session = CreateSession("company_accounting_settings");

        var decision = BusinessApprovalAuthority.EvaluateGrIrSettlementExecution(
            session,
            "execute");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanExecuteGrIrSettlement_BlocksOrdinaryUserRole()
    {
        var session = CreateSession("user", "ap");

        var decision = BusinessApprovalAuthority.EvaluateGrIrSettlementExecution(
            session,
            "post");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_grir_settlement_execution_authority", decision.OutcomeCode);
        Assert.Contains("accounting-governance", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanApprovePurchaseOrder_AllowsApprovePermissionToken()
    {
        var session = CreateSession("user", "approve");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
            session,
            "approve",
            9_999.99m);

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanApprovePurchaseOrder_BlocksApprovePermissionAboveThreshold()
    {
        var session = CreateSession("user", "approve");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
            session,
            "approve",
            BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount + 0.01m);

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_purchase_order_approval_threshold", decision.OutcomeCode);
        Assert.Contains("governance approval", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanApprovePurchaseOrder_AllowsOwnerAboveThreshold()
    {
        var session = CreateSession("owner");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
            session,
            "approve",
            BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount + 10_000m);

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanApprovePurchaseOrder_BlocksOrdinaryApRole()
    {
        var session = CreateSession("user", "ap");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
            session,
            "approve");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_purchase_order_approval_authority", decision.OutcomeCode);
        Assert.Contains("approval user", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanReleasePurchaseOrder_AllowsBookGovernanceRole()
    {
        var session = CreateSession("company_book_governance");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderRelease(
            session,
            "release");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanReopenPurchaseOrderForAmendment_BlocksApproveOnlyRole()
    {
        var session = CreateSession("approve");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderAmendment(
            session,
            "reopen_for_amendment");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_purchase_order_amendment_authority", decision.OutcomeCode);
        Assert.Contains("book-governance", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanReopenPurchaseOrderForAmendment_AllowsOwnerRole()
    {
        var session = CreateSession("owner");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderAmendment(
            session,
            "reopen_for_amendment");

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
