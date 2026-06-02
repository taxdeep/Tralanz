namespace Citus.Accounting.Api;

public sealed record BusinessEndpointReadGateResult(
    bool Allowed,
    int StatusCode,
    string OutcomeCode,
    string Message,
    IResult? Response);

public static class BusinessEndpointReadGate
{
    public static BusinessEndpointReadGateResult ValidateCompanyScopedRead(
        BusinessSessionContext? session,
        CompanyId companyId,
        string moduleCode,
        string operationCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationCode);

        if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
        {
            return new BusinessEndpointReadGateResult(
                false,
                StatusCodes.Status401Unauthorized,
                "business_session_required",
                "A business session is required to read company-scoped data.",
                Results.Unauthorized());
        }

        if (string.IsNullOrEmpty(companyId.Value) ||
            !string.Equals(companyId.Value, session.ActiveCompanyId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return new BusinessEndpointReadGateResult(
                false,
                StatusCodes.Status403Forbidden,
                "active_company_mismatch",
                "The requested company must match the active business session company.",
                Results.Forbid());
        }

        var decision = BusinessApprovalAuthority.EvaluateBusinessOperation(
            session,
            moduleCode,
            operationCode);

        if (!decision.Allowed)
        {
            return new BusinessEndpointReadGateResult(
                false,
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

        return new BusinessEndpointReadGateResult(
            true,
            StatusCodes.Status200OK,
            "company_scoped_read_allowed",
            "The company-scoped read is allowed.",
            null);
    }
}
