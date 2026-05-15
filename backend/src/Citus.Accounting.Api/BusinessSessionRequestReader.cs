using Citus.Ui.Shared.Business;
using Microsoft.Extensions.Primitives;

namespace Citus.Accounting.Api;

public sealed class BusinessSessionRequestReader
{
    public bool TryRead(IHeaderDictionary headers, out BusinessSessionContext? context, out string? error)
    {
        context = null;
        error = null;

        if (!TryReadHeaderValue(
                headers,
                BusinessSessionHeaders.UserId,
                BusinessSessionHeaderNames.LegacyUserId,
                out var userIdValue))
        {
            error = $"Missing required business session header '{BusinessSessionHeaders.UserId}' or '{BusinessSessionHeaderNames.LegacyUserId}'.";
            return false;
        }

        if (!TryReadHeaderValue(
                headers,
                BusinessSessionHeaders.ActiveCompanyId,
                BusinessSessionHeaderNames.LegacyActiveCompanyId,
                out var companyIdValue))
        {
            error = $"Missing required business session header '{BusinessSessionHeaders.ActiveCompanyId}' or '{BusinessSessionHeaderNames.LegacyActiveCompanyId}'.";
            return false;
        }

        if (!UserId.TryParse(userIdValue, out var userId))
        {
            error = $"Header '{BusinessSessionHeaders.UserId}' must be a valid user id.";
            return false;
        }

        if (!CompanyId.TryParse(companyIdValue, out var activeCompanyId))
        {
            error = $"Header '{BusinessSessionHeaders.ActiveCompanyId}' must be a valid company id.";
            return false;
        }

        context = new BusinessSessionContext
        {
            UserId = userId,
            ActiveCompanyId = activeCompanyId
        };

        return true;
    }

    public bool TryReadSessionToken(IHeaderDictionary headers, out string token)
    {
        return TryReadHeaderValue(
            headers,
            BusinessAuthHeaderNames.SessionToken,
            BusinessAuthHeaderNames.LegacySessionToken,
            out token);
    }

    private static bool TryReadHeaderValue(
        IHeaderDictionary headers,
        string primaryKey,
        string legacyKey,
        out string value)
    {
        value = string.Empty;

        if (!headers.TryGetValue(primaryKey, out StringValues values) &&
            !headers.TryGetValue(legacyKey, out values))
        {
            return false;
        }

        var candidate = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate.Trim();
        return true;
    }
}
