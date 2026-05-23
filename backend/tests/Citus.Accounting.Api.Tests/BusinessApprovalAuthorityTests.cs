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
    public void CanAccessBankReconciliation_AllowsReconciliationPermissionToken()
    {
        var session = CreateSession("user", "reconciliation");

        var decision = BusinessApprovalAuthority.EvaluateBankReconciliationAccess(
            session,
            "complete");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void CanAccessBankReconciliation_BlocksOrdinaryApRole()
    {
        var session = CreateSession("user", "ap");

        var decision = BusinessApprovalAuthority.EvaluateBankReconciliationAccess(
            session,
            "complete");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_bank_reconciliation_authority", decision.OutcomeCode);
        Assert.Contains("reconciliation user", decision.Message, StringComparison.Ordinal);
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

    [Fact]
    public void CanReversePurchaseOrderApproval_BlocksApproveOnlyRole()
    {
        var session = CreateSession("approve");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApprovalReversal(
            session,
            "reverse_approval");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_purchase_order_approval_reversal_authority", decision.OutcomeCode);
        Assert.Contains("book-governance", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanReversePurchaseOrderApproval_AllowsBookGovernanceRole()
    {
        var session = CreateSession("book_governance");

        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApprovalReversal(
            session,
            "reverse_approval");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsArUserToPostInvoices()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("ar"),
            "sales",
            "post invoices");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksApUserFromPostingInvoices()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("ap"),
            "sales",
            "post invoices");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsApUserToPostBills()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("ap"),
            "purchases",
            "post bills");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsPaymentsUserToPostSettlements()
    {
        var receivePaymentDecision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("payments"),
            "ar_payments",
            "post receive payments");
        var payBillDecision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("payments"),
            "ap_payments",
            "post pay bills");

        Assert.True(receivePaymentDecision.Allowed);
        Assert.True(payBillDecision.Allowed);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksArUserFromPostingManualJournals()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("ar"),
            "accounting",
            "post manual journals");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsBookGovernanceToPostManualJournals()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("company_book_governance"),
            "accounting",
            "post manual journals");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksArUserFromVoidingJournalEntries()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("ar"),
            "accounting",
            "void journal entries");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsOwnerToVoidJournalEntries()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("owner"),
            "accounting",
            "void journal entries");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsReportsUserToExportReports()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("reports"),
            "reports",
            "export reports");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksArUserFromExportingReports()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("ar"),
            "reports",
            "export reports");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsInventoryUserToManageInventory()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("inventory"),
            "inventory",
            "save inventory receipts");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksReportsUserFromManagingInventory()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("reports"),
            "inventory",
            "save inventory receipts");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsTaskUserToUpdateTasks()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("tasks"),
            "tasks",
            "update action-center tasks");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksSalesUserFromUpdatingTasks()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("sales"),
            "tasks",
            "update action-center tasks");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_AllowsBankingUserToPostBankingDocuments()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("banking"),
            "banking",
            "save and post bank deposits");

        Assert.True(decision.Allowed);
        Assert.Equal("authority_allowed", decision.OutcomeCode);
    }

    [Fact]
    public void EvaluateBusinessOperation_BlocksSalesUserFromPostingBankingDocuments()
    {
        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            CreateSession("sales"),
            "banking",
            "save and post bank deposits");

        Assert.False(decision.Allowed);
        Assert.Equal("blocked_business_operation_authority", decision.OutcomeCode);
    }

    private static BusinessSessionContext CreateSession(params string[] roles) =>
        new()
        {
            UserId = UserId.FromOrdinal(1),
            ActiveCompanyId = CompanyId.FromOrdinal(1),
            Roles = roles
        };
}
