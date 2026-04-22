using Citus.Ui.Shared.Business;
using Citus.Platform.Core.Runtime;

namespace Citus.Accounting.Api;

public sealed class BusinessRouteGuard(
    BusinessSessionRequestReader sessionReader,
    BusinessRequestContractGuard contractGuard,
    BusinessSessionDirectory sessionDirectory)
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

        if (!sessionDirectory.TryResolve(session, out var resolution, out var directoryError))
        {
            return BusinessRequestGuardResult.Reject(
                directoryError ?? "The business session is not authorized for the requested company context.",
                StatusCodes.Status403Forbidden);
        }

        var companyWriteGate = EvaluateCompanyStatusForWrite(httpMethod, resolution?.ActiveCompany);
        if (companyWriteGate is not null)
        {
            return companyWriteGate with
            {
                Resolution = resolution
            };
        }

        var resolvedSession = session with
        {
            Roles = resolution?.User.Roles ?? Array.Empty<string>()
        };

        var validation = contractGuard.Validate(arguments, resolvedSession);
        return validation with
        {
            Session = resolvedSession,
            Resolution = resolution
        };
    }

    private static bool IsWriteMethod(string httpMethod) =>
        HttpMethods.IsPost(httpMethod) ||
        HttpMethods.IsPut(httpMethod) ||
        HttpMethods.IsPatch(httpMethod) ||
        HttpMethods.IsDelete(httpMethod);

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
