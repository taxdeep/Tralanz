using Citus.Platform.Core.Abstractions;
using Citus.Platform.Core.Runtime;
using Citus.Ui.Shared.Control;

namespace Citus.Accounting.Api;

public sealed record SysAdminControlPlaneGateResult(
    bool Allowed,
    UserId? SysAdminAccountId,
    int StatusCode,
    string OutcomeCode,
    string Message,
    IResult? Response);

public static class SysAdminControlPlaneGate
{
    public static async Task<SysAdminControlPlaneGateResult> ValidateAsync(
        HttpContext httpContext,
        ISysAdminAuthRepository authRepository,
        CancellationToken cancellationToken)
    {
        var sessionToken = httpContext.Request.Headers[SysAdminAuthConstants.SessionHeaderName]
            .ToString()
            .Trim();

        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return Blocked(
                StatusCodes.Status401Unauthorized,
                "missing_sysadmin_session",
                $"Missing required SysAdmin session header '{SysAdminAuthConstants.SessionHeaderName}'.");
        }

        var validation = await authRepository.ValidateSessionAsync(sessionToken, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            return Blocked(
                StatusCodes.Status401Unauthorized,
                string.IsNullOrWhiteSpace(validation.FailureCode)
                    ? "invalid_sysadmin_session"
                    : validation.FailureCode,
                string.IsNullOrWhiteSpace(validation.FailureMessage)
                    ? "SysAdmin session is invalid."
                    : validation.FailureMessage);
        }

        return new SysAdminControlPlaneGateResult(
            true,
            validation.SysAdminAccountId,
            StatusCodes.Status200OK,
            "sysadmin_session_allowed",
            "SysAdmin session is valid.",
            null);
    }

    private static SysAdminControlPlaneGateResult Blocked(
        int statusCode,
        string outcomeCode,
        string message) =>
        new(
            false,
            null,
            statusCode,
            outcomeCode,
            message,
            Results.Json(
                new
                {
                    outcomeCode,
                    message
                },
                statusCode: statusCode));
}
