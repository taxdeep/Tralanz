using Citus.Platform.Core.Runtime;

namespace Citus.Accounting.Api;

public sealed class BusinessRouteGuard(
    BusinessSessionRequestReader sessionReader,
    BusinessRequestContractGuard contractGuard,
    BusinessSessionDirectory sessionDirectory)
{
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

        if (!sessionDirectory.TryResolve(session, out _, out var directoryError))
        {
            return BusinessRequestGuardResult.Reject(
                directoryError ?? "The business session is not authorized for the requested company context.",
                StatusCodes.Status403Forbidden);
        }

        var validation = contractGuard.Validate(arguments, session);
        return validation with
        {
            Session = session
        };
    }

    private static bool IsWriteMethod(string httpMethod) =>
        HttpMethods.IsPost(httpMethod) ||
        HttpMethods.IsPut(httpMethod) ||
        HttpMethods.IsPatch(httpMethod) ||
        HttpMethods.IsDelete(httpMethod);
}
