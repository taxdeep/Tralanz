using Citus.Platform.Core.Abstractions;
using Citus.Ui.Shared.Business;
using Citus.Platform.Core.Runtime;

namespace Citus.Accounting.Api;

public sealed class BusinessRouteGuard(
    BusinessSessionRequestReader sessionReader,
    BusinessRequestContractGuard contractGuard,
    BusinessSessionDirectory sessionDirectory,
    IPlatformBusinessSessionRepository sessionRepository)
{
    public async Task<BusinessRequestGuardResult> EvaluateAsync(
        string httpMethod,
        IHeaderDictionary headers,
        IReadOnlyList<object?> arguments,
        PlatformMaintenanceState? maintenanceState,
        CancellationToken cancellationToken)
    {
        if (IsWriteMethod(httpMethod) && maintenanceState?.Enabled == true)
        {
            return BusinessRequestGuardResult.Reject(
                string.IsNullOrWhiteSpace(maintenanceState.Message)
                    ? "Business writes are blocked because maintenance mode is enabled."
                    : maintenanceState.Message,
                StatusCodes.Status503ServiceUnavailable);
        }

        if (!sessionReader.TryRead(headers, out var session, out var error))
        {
            return BusinessRequestGuardResult.Reject(
                error ?? "Business session headers are required for company-scoped access.",
                StatusCodes.Status401Unauthorized);
        }

        if (session is null)
        {
            return BusinessRequestGuardResult.Reject(
                "Business session context could not be resolved from the request headers.",
                StatusCodes.Status401Unauthorized);
        }

        var tokenResult = await ValidateSessionTokenAsync(headers, cancellationToken);
        if (!tokenResult.Allowed)
        {
            return tokenResult;
        }

        if (tokenResult.Session is null || tokenResult.Session.UserId != session.UserId)
        {
            return BusinessRequestGuardResult.Reject(
                "The business session token does not match the requested user context.",
                StatusCodes.Status401Unauthorized);
        }

        // M17 (AUDIT_2026-05-20 P2-4): bind the session token to the
        // ActiveCompanyId. Without this check, a stolen token can be
        // used against any company the user is a member of, expanding
        // the blast radius from one company to N. The token's
        // active_company_id is stamped at login (or on the dedicated
        // switch-active-company endpoint) and persisted on the
        // business_sessions row; we reject when the request's header
        // claims a different company than the row carries.
        //
        // Legitimate company switching goes through
        // `POST /accounting/business-session/switch-active-company`
        // which updates the row AND returns the new
        // ActiveCompanyId so the client can flip its header in lockstep.
        if (tokenResult.Session.ActiveCompanyId != session.ActiveCompanyId)
        {
            return BusinessRequestGuardResult.Reject(
                "The business session token is not bound to the requested active company. " +
                "Switch the active company via /business-session/switch-active-company before retrying.",
                StatusCodes.Status401Unauthorized);
        }

        var directoryResult = await sessionDirectory.ResolveAsync(session, cancellationToken);
        if (!directoryResult.Success)
        {
            return BusinessRequestGuardResult.Reject(
                directoryResult.Error ?? "The business session is not authorized for the requested company context.",
                StatusCodes.Status403Forbidden);
        }

        var companyWriteGate = EvaluateCompanyStatusForWrite(httpMethod, directoryResult.Resolution?.ActiveCompany);
        if (companyWriteGate is not null)
        {
            return companyWriteGate with
            {
                Resolution = directoryResult.Resolution
            };
        }

        var resolvedSession = session with
        {
            Roles = directoryResult.Resolution?.User.Roles ?? Array.Empty<string>()
        };

        var validation = contractGuard.Validate(arguments, resolvedSession);
        return validation with
        {
            Session = resolvedSession,
            Resolution = directoryResult.Resolution
        };
    }

    public BusinessRequestGuardResult Evaluate(
        string httpMethod,
        IHeaderDictionary headers,
        IReadOnlyList<object?> arguments,
        PlatformMaintenanceState? maintenanceState)
    {
        if (IsWriteMethod(httpMethod) && maintenanceState?.Enabled == true)
        {
            return BusinessRequestGuardResult.Reject(
                string.IsNullOrWhiteSpace(maintenanceState.Message)
                    ? "Business writes are blocked because maintenance mode is enabled."
                    : maintenanceState.Message,
                StatusCodes.Status503ServiceUnavailable);
        }

        if (!sessionReader.TryRead(headers, out var session, out var error))
        {
            return BusinessRequestGuardResult.Reject(
                error ?? "Business session headers are required for company-scoped access.",
                StatusCodes.Status401Unauthorized);
        }

        if (session is null)
        {
            return BusinessRequestGuardResult.Reject(
                "Business session context could not be resolved from the request headers.",
                StatusCodes.Status401Unauthorized);
        }

        return BusinessRequestGuardResult.Reject(
            "Business session token validation requires the asynchronous accounting route guard.",
            StatusCodes.Status500InternalServerError);

    }

    private static bool IsWriteMethod(string httpMethod) =>
        HttpMethods.IsPost(httpMethod) ||
        HttpMethods.IsPut(httpMethod) ||
        HttpMethods.IsPatch(httpMethod) ||
        HttpMethods.IsDelete(httpMethod);

    private async Task<BusinessRequestGuardResult> ValidateSessionTokenAsync(
        IHeaderDictionary headers,
        CancellationToken cancellationToken)
    {
        if (!sessionReader.TryReadSessionToken(headers, out var token))
        {
            return BusinessRequestGuardResult.Reject(
                $"Missing required business session token header '{BusinessAuthHeaderNames.SessionToken}' or '{BusinessAuthHeaderNames.LegacySessionToken}'.",
                StatusCodes.Status401Unauthorized);
        }

        var result = await sessionRepository.ValidateSessionAsync(token, cancellationToken);
        if (!result.Succeeded)
        {
            return BusinessRequestGuardResult.Reject(
                string.IsNullOrWhiteSpace(result.FailureMessage)
                    ? "The business session token is invalid or expired."
                    : result.FailureMessage,
                StatusCodes.Status401Unauthorized);
        }

        return new BusinessRequestGuardResult
        {
            Allowed = true,
            StatusCode = StatusCodes.Status200OK,
            Session = new BusinessSessionContext
            {
                UserId = result.UserId,
                ActiveCompanyId = result.ActiveCompanyId
            },
            Resolution = null
        };
    }

    private static BusinessRequestGuardResult? EvaluateCompanyStatusForWrite(
        string httpMethod,
        BusinessCompanySummary? activeCompany)
    {
        if (!IsWriteMethod(httpMethod) || activeCompany?.IsReadOnly != true)
        {
            return null;
        }

        var status = string.IsNullOrWhiteSpace(activeCompany.Status)
            ? "inactive"
            : activeCompany.Status.Trim().ToLowerInvariant();

        return BusinessRequestGuardResult.Reject(
            $"Company '{activeCompany.CompanyName}' is '{status}' and allows read-only access only.",
            StatusCodes.Status409Conflict);
    }
}
