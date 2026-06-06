using System.Security.Cryptography;
using System.Text;

namespace Citus.Accounting.Api.Authorization;

/// <summary>
/// Shared authorization gate for the /internal/ai/* manual triggers.
///
/// Validates the <c>UnityAi:ManualTriggerBootstrapToken</c> bearer token
/// (constant-time compare), requires a companyId, and — the tenant binding —
/// restricts the token to a configured set of companies via
/// <c>UnityAi:ManualTriggerAllowedCompanyIds</c> (comma/semicolon separated).
///
/// When the allow-list is empty the prior behavior is preserved (the token may
/// act on any company), so deploying this change is non-breaking; an operator
/// opts into tenant binding by configuring the list. When the list is set, a
/// request for a companyId outside it is rejected with 403 and audit-logged.
///
/// Returns <c>null</c> when the caller may proceed; otherwise the IResult to
/// return. Token bytes are never logged (only a short SHA-256 fingerprint).
/// </summary>
public static class InternalAiTriggerGate
{
    public static IResult? Authorize(
        HttpContext httpContext,
        IConfiguration configuration,
        CompanyId companyId,
        string endpointLabel,
        ILogger logger)
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var configuredToken = configuration["UnityAi:ManualTriggerBootstrapToken"];
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return Results.Problem(
                detail: "This internal AI trigger is disabled because UnityAi:ManualTriggerBootstrapToken is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authValues) || authValues.Count == 0)
        {
            return Results.Unauthorized();
        }

        var providedAuth = authValues.ToString();
        if (!providedAuth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        var providedToken = providedAuth.AsSpan("Bearer ".Length).Trim().ToString();
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        if (providedBytes.Length != configuredBytes.Length
            || !CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
        {
            logger.LogWarning(
                "AUDIT internal-ai-trigger rejected (bad token): endpoint={Endpoint} remoteIp={RemoteIp}",
                endpointLabel, remoteIp);
            return Results.Unauthorized();
        }

        if (companyId.Value is null)
        {
            return Results.BadRequest(new { message = "companyId query parameter is required." });
        }

        // Tenant binding: when an allow-list is configured the token may only
        // act on those company ids. Empty list preserves prior behavior.
        var allowedCompanyIds = (configuration["UnityAi:ManualTriggerAllowedCompanyIds"] ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowedCompanyIds.Length > 0
            && !allowedCompanyIds.Any(id => string.Equals(id, companyId.Value, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning(
                "AUDIT internal-ai-trigger rejected (company not allow-listed): endpoint={Endpoint} company={CompanyId} remoteIp={RemoteIp}",
                endpointLabel, companyId.Value, remoteIp);
            return Results.Problem(
                detail: "This trigger token is not authorized for the requested companyId.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var tokenFingerprint = Convert.ToHexString(SHA256.HashData(providedBytes)).ToLowerInvariant()[..12];
        logger.LogInformation(
            "AUDIT internal-ai-trigger authorized: endpoint={Endpoint} company={CompanyId} tokenFp={TokenFingerprint} remoteIp={RemoteIp}",
            endpointLabel, companyId.Value, tokenFingerprint, remoteIp);

        return null;
    }
}
