namespace Citus.Accounting.Api;

public sealed record BusinessEndpointMutationGateResult(
    bool Allowed,
    UserId ActorId,
    int StatusCode,
    string OutcomeCode,
    string Message,
    IResult? Response);

public static class BusinessEndpointMutationGate
{
    public static BusinessEndpointMutationGateResult ValidateCompanyScopedMutation(
        BusinessSessionContext? session,
        CompanyId companyId,
        string moduleCode,
        string operationCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationCode);

        if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
        {
            return Blocked(
                StatusCodes.Status401Unauthorized,
                "business_session_required",
                "A business session is required to change company-scoped data.");
        }

        if (string.IsNullOrEmpty(companyId.Value) ||
            !string.Equals(companyId.Value, session.ActiveCompanyId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                StatusCodes.Status403Forbidden,
                "active_company_mismatch",
                "The requested company must match the active business session company.",
                Results.Forbid());
        }

        if (string.IsNullOrEmpty(session.UserId.Value))
        {
            return Blocked(
                StatusCodes.Status401Unauthorized,
                "business_actor_required",
                "A business session actor is required to change company-scoped data.");
        }

        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            session,
            moduleCode,
            operationCode);

        if (!decision.Allowed)
        {
            return Blocked(
                StatusCodes.Status403Forbidden,
                decision.OutcomeCode,
                decision.Message,
                Results.Json(
                    new
                    {
                        moduleCode,
                        operationCode,
                        outcomeCode = decision.OutcomeCode,
                        message = decision.Message
                    },
                    statusCode: StatusCodes.Status403Forbidden));
        }

        return new BusinessEndpointMutationGateResult(
            true,
            session.UserId,
            StatusCodes.Status200OK,
            "company_scoped_mutation_allowed",
            "The company-scoped mutation is allowed.",
            null);
    }

    private static BusinessEndpointMutationGateResult Blocked(
        int statusCode,
        string outcomeCode,
        string message,
        IResult? response = null) =>
        new(
            false,
            default,
            statusCode,
            outcomeCode,
            message,
            response ?? Results.Json(
                new
                {
                    outcomeCode,
                    message
                },
                statusCode: statusCode));
}
