namespace Citus.Accounting.Api;

public static class BusinessApprovalAuthority
{
    public static readonly IReadOnlyList<string> OpenItemAdjustmentApprovalRoles =
    [
        "owner",
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

    private static bool IsOpenItemAdjustmentApprovalRole(string role) =>
        OpenItemAdjustmentApprovalRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    public sealed record Decision(
        bool Allowed,
        string OutcomeCode,
        string Message);
}
