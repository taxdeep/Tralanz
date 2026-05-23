using Citus.Accounting.Application.Repositories;

namespace Citus.Accounting.Api;

public static class BusinessApprovalAuthority
{
    public const decimal PurchaseOrderApprovalGovernanceThresholdAmount =
        PurchaseOrderApprovalThresholdPolicy.TemporaryGovernanceThresholdAmount;

    public static readonly IReadOnlyList<string> OpenItemAdjustmentApprovalRoles =
    [
        "owner",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    public static readonly IReadOnlyList<string> PurchaseOrderApprovalRoles =
    [
        "owner",
        "approve",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    public static readonly IReadOnlyList<string> PurchaseOrderAmendmentRoles =
    [
        "owner",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    public static readonly IReadOnlyList<string> BankReconciliationRoles =
    [
        "owner",
        "reconciliation",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> AccountingPostRoles =
    [
        "owner",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> SalesPostRoles =
    [
        "owner",
        "ar",
        "sales",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> PurchasePostRoles =
    [
        "owner",
        "ap",
        "purchases",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> ArPaymentPostRoles =
    [
        "owner",
        "ar",
        "sales",
        "payments",
        "banking",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> ApPaymentPostRoles =
    [
        "owner",
        "ap",
        "purchases",
        "payments",
        "banking",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> ReportsExportRoles =
    [
        "owner",
        "reports",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> InventoryOperationRoles =
    [
        "owner",
        "inventory",
        "ap",
        "purchases",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> TaskOperationRoles =
    [
        "owner",
        "tasks",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    private static readonly IReadOnlyList<string> BankingOperationRoles =
    [
        "owner",
        "payments",
        "banking",
        "book_governance",
        "company_book_governance",
        "company_accounting_settings"
    ];

    public static bool CanApproveOpenItemAdjustment(BusinessSessionContext? session) =>
        session?.Roles.Any(IsOpenItemAdjustmentApprovalRole) == true;

    public static bool CanManageOpenItemAdjustmentAccountMapping(BusinessSessionContext? session) =>
        session?.Roles.Any(IsOpenItemAdjustmentApprovalRole) == true;

    public static bool CanManageGrIrClearingAccountPolicy(BusinessSessionContext? session) =>
        session?.Roles.Any(IsOpenItemAdjustmentApprovalRole) == true;

    public static bool CanExecuteGrIrSettlement(BusinessSessionContext? session) =>
        session?.Roles.Any(IsOpenItemAdjustmentApprovalRole) == true;

    /// <summary>
    /// M7 iter 1: only owners / book-governance roles can transition
    /// accounting periods. Reuses the OpenItemAdjustment role set
    /// because the authority profile is the same — both touch the
    /// books in a way that the operator role shouldn't unilaterally
    /// trigger.
    /// </summary>
    public static bool CanTransitionAccountingPeriod(BusinessSessionContext? session) =>
        session?.Roles.Any(IsOpenItemAdjustmentApprovalRole) == true;

    public static bool CanAccessBankReconciliation(BusinessSessionContext? session) =>
        session?.Roles.Any(IsBankReconciliationRole) == true;

    public static Decision EvaluateBusinessOperation(
        BusinessSessionContext? session,
        string moduleCode,
        string operationCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationCode);

        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {operationCode} in {moduleCode}.");
        }

        var allowedRoles = ResolveBusinessOperationRoles(moduleCode);
        if (!session.Roles.Any(role => allowedRoles.Contains(NormalizeRole(role), StringComparer.Ordinal)))
        {
            return new Decision(
                false,
                "blocked_business_operation_authority",
                $"The current business session is not authorized to {operationCode} in {moduleCode}.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {operationCode} in {moduleCode}.");
    }

    public static bool CanApprovePurchaseOrder(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderApprovalRole) == true;

    public static bool CanApprovePurchaseOrderAboveThreshold(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderGovernanceApprovalRole) == true;

    public static bool CanReleasePurchaseOrder(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderApprovalRole) == true;

    public static bool CanReopenPurchaseOrderForAmendment(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderAmendmentRole) == true;

    public static bool CanReversePurchaseOrderApproval(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderAmendmentRole) == true;

    public static bool CanClosePurchaseOrder(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderAmendmentRole) == true;

    public static bool CanCancelPurchaseOrder(BusinessSessionContext? session) =>
        session?.Roles.Any(IsPurchaseOrderAmendmentRole) == true;

    public static Decision EvaluateOpenItemAdjustmentApproval(
        BusinessSessionContext? session,
        string openItemLabel,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} a governed {openItemLabel} open item adjustment request.");
        }

        if (!CanApproveOpenItemAdjustment(session))
        {
            return new Decision(
                false,
                "blocked_approval_authority",
                $"Only a company owner or book-governance user can {transitionCode} a governed {openItemLabel} open item adjustment request.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the governed {openItemLabel} open item adjustment request.");
    }

    public static Decision EvaluateOpenItemAdjustmentAccountMappingManagement(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} open-item adjustment account mappings.");
        }

        if (!CanManageOpenItemAdjustmentAccountMapping(session))
        {
            return new Decision(
                false,
                "blocked_mapping_management_authority",
                $"Only a company owner or book-governance user can {transitionCode} open-item adjustment account mappings.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} open-item adjustment account mappings.");
    }

    public static Decision EvaluateGrIrClearingAccountPolicyManagement(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} the GR/IR clearing account policy.");
        }

        if (!CanManageGrIrClearingAccountPolicy(session))
        {
            return new Decision(
                false,
                "blocked_grir_policy_management_authority",
                $"Only a company owner or accounting-governance user can {transitionCode} the GR/IR clearing account policy.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the GR/IR clearing account policy.");
    }

    public static Decision EvaluateGrIrSettlementExecution(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} GR/IR settlement.");
        }

        if (!CanExecuteGrIrSettlement(session))
        {
            return new Decision(
                false,
                "blocked_grir_settlement_execution_authority",
                $"Only a company owner or accounting-governance user can {transitionCode} GR/IR settlement.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} GR/IR settlement.");
    }

    public static Decision EvaluatePurchaseOrderApproval(
        BusinessSessionContext? session,
        string transitionCode,
        decimal? estimatedOrderAmount = null)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} a purchase order.");
        }

        if (!CanApprovePurchaseOrder(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_approval_authority",
                $"Only a company owner, approval user, or book-governance user can {transitionCode} a purchase order.");
        }

        if (RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount) &&
            !CanApprovePurchaseOrderAboveThreshold(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_approval_threshold",
                $"Purchase orders above {PurchaseOrderApprovalGovernanceThresholdAmount:N2} require company owner or governance approval.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the purchase order.");
    }

    public static Decision EvaluateBankReconciliationAccess(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} bank reconciliation.");
        }

        if (!CanAccessBankReconciliation(session))
        {
            return new Decision(
                false,
                "blocked_bank_reconciliation_authority",
                $"Only a company owner, reconciliation user, or accounting-governance user can {transitionCode} bank reconciliation.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} bank reconciliation.");
    }

    public static bool RequiresPurchaseOrderGovernanceApproval(decimal? estimatedOrderAmount) =>
        PurchaseOrderApprovalThresholdPolicy.RequiresGovernanceApproval(estimatedOrderAmount);

    public static Decision EvaluatePurchaseOrderRelease(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} a purchase order.");
        }

        if (!CanReleasePurchaseOrder(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_release_authority",
                $"Only a company owner, approval user, or book-governance user can {transitionCode} a purchase order.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the purchase order.");
    }

    public static Decision EvaluatePurchaseOrderAmendment(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} a purchase order for amendment.");
        }

        if (!CanReopenPurchaseOrderForAmendment(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_amendment_authority",
                $"Only a company owner or book-governance user can {transitionCode} a purchase order for amendment.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the purchase order for amendment.");
    }

    public static Decision EvaluatePurchaseOrderApprovalReversal(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} purchase order approval.");
        }

        if (!CanReversePurchaseOrderApproval(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_approval_reversal_authority",
                $"Only a company owner or book-governance user can {transitionCode} purchase order approval.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} purchase order approval.");
    }

    public static Decision EvaluatePurchaseOrderClose(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} a purchase order.");
        }

        if (!CanClosePurchaseOrder(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_close_authority",
                $"Only a company owner or book-governance user can {transitionCode} a purchase order.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the purchase order.");
    }

    public static Decision EvaluatePurchaseOrderCancel(
        BusinessSessionContext? session,
        string transitionCode)
    {
        if (session is null)
        {
            return new Decision(
                false,
                "blocked_session_required",
                $"A business session is required to {transitionCode} a purchase order.");
        }

        if (!CanCancelPurchaseOrder(session))
        {
            return new Decision(
                false,
                "blocked_purchase_order_cancel_authority",
                $"Only a company owner or book-governance user can {transitionCode} a purchase order.");
        }

        return new Decision(
            true,
            "authority_allowed",
            $"The business session has authority to {transitionCode} the purchase order.");
    }

    private static bool IsOpenItemAdjustmentApprovalRole(string role) =>
        OpenItemAdjustmentApprovalRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    private static bool IsPurchaseOrderApprovalRole(string role) =>
        PurchaseOrderApprovalRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    private static bool IsPurchaseOrderGovernanceApprovalRole(string role) =>
        PurchaseOrderAmendmentRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    private static bool IsPurchaseOrderAmendmentRole(string role) =>
        PurchaseOrderAmendmentRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    private static bool IsBankReconciliationRole(string role) =>
        BankReconciliationRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    private static IReadOnlyList<string> ResolveBusinessOperationRoles(string moduleCode) =>
        NormalizeRole(moduleCode) switch
        {
            "accounting" => AccountingPostRoles,
            "sales" => SalesPostRoles,
            "purchases" => PurchasePostRoles,
            "ar_payments" => ArPaymentPostRoles,
            "ap_payments" => ApPaymentPostRoles,
            "reports" => ReportsExportRoles,
            "inventory" => InventoryOperationRoles,
            "tasks" => TaskOperationRoles,
            "banking" => BankingOperationRoles,
            _ => Array.Empty<string>()
        };

    private static string NormalizeRole(string role) =>
        role.Trim().ToLowerInvariant();

    public sealed record Decision(
        bool Allowed,
        string OutcomeCode,
        string Message);
}
