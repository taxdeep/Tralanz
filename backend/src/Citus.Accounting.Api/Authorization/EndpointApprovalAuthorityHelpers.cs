using Citus.Accounting.Api;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Api.Authorization;

/// <summary>
/// Endpoint authorization-decision helpers extracted verbatim from Program.cs
/// (P1, behavior-preserving). Each turns a BusinessApprovalAuthority decision
/// into a 403 IResult (or null when allowed) for the PO / open-item / GR-IR
/// lifecycle endpoints, plus the small PO estimated-amount helpers.
/// </summary>
public static class EndpointApprovalAuthorityHelpers
{
    public static IResult? RequireOpenItemAdjustmentApprovalAuthority(
        BusinessSessionContext? session,
        OpenItemAdjustmentRequestRecord request,
        string openItemLabel,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentApproval(
            session,
            openItemLabel,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new OpenItemAdjustmentRequestTransitionResult(
                    request,
                    transitionCode,
                    decision.OutcomeCode,
                    decision.Message),
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequireOpenItemAdjustmentAccountMappingManagementAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluateOpenItemAdjustmentAccountMappingManagement(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequireGrIrClearingAccountPolicyManagementAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluateGrIrClearingAccountPolicyManagement(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequireGrIrSettlementExecutionAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluateGrIrSettlementExecution(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequirePurchaseOrderApprovalAuthority(
        BusinessSessionContext? session,
        string transitionCode,
        decimal? estimatedOrderAmount = null)
    {
        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApproval(
            session,
            transitionCode,
            estimatedOrderAmount);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    estimatedOrderAmount,
                    approvalThresholdAmount = BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequirePurchaseOrderReleaseAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderRelease(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequirePurchaseOrderAmendmentAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderAmendment(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequirePurchaseOrderApprovalReversalAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderApprovalReversal(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequirePurchaseOrderCloseAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderClose(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult? RequirePurchaseOrderCancelAuthority(
        BusinessSessionContext? session,
        string transitionCode)
    {
        var decision = BusinessApprovalAuthority.EvaluatePurchaseOrderCancel(
            session,
            transitionCode);

        return decision.Allowed
            ? null
            : Results.Json(
                new
                {
                    transitionCode,
                    outcomeCode = decision.OutcomeCode,
                    message = decision.Message
                },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static decimal? CalculatePurchaseOrderListEstimatedAmount(PurchaseOrderDocumentListItem document) => null;

    public static decimal? CalculatePurchaseOrderDocumentEstimatedAmount(PurchaseOrderDocument document) =>
        document.PurchaseOrderLines.Any(static line => !line.UnitCost.HasValue)
            ? null
            : document.PurchaseOrderLines.Sum(static line => line.OrderedQuantity * line.UnitCost!.Value);

    public static object BuildPurchaseOrderApprovalAuthoritySummary(decimal? estimatedOrderAmount) =>
        new
        {
            EstimatedOrderAmount = estimatedOrderAmount,
            ThresholdAmount = BusinessApprovalAuthority.PurchaseOrderApprovalGovernanceThresholdAmount,
            RequiresGovernanceApproval = BusinessApprovalAuthority.RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount),
            Summary = !estimatedOrderAmount.HasValue
                ? "Estimated purchase order amount is unavailable, so the temporary threshold does not add an approval block yet."
                : BusinessApprovalAuthority.RequiresPurchaseOrderGovernanceApproval(estimatedOrderAmount)
                ? "Purchase order approval is above the temporary governance threshold and requires owner or governance authority."
                : "Purchase order approval is within the temporary approver threshold."
        };
}
