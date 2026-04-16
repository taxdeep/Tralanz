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

    private static bool IsOpenItemAdjustmentApprovalRole(string role) =>
        OpenItemAdjustmentApprovalRoles.Contains(
            role.Trim().ToLowerInvariant(),
            StringComparer.Ordinal);

    public sealed record Decision(
        bool Allowed,
        string OutcomeCode,
        string Message);
}
