using Microsoft.Extensions.Primitives;

namespace Citus.Accounting.Api;

public sealed class BusinessSessionRequestReader
{
    public bool TryRead(IHeaderDictionary headers, out BusinessSessionContext? context, out string? error)
    {
        context = null;
        error = null;

        if (!TryReadHeaderValue(headers, BusinessSessionHeaders.UserId, out var userIdValue))
        {
            error = $"Missing required business session header '{BusinessSessionHeaders.UserId}'.";
            return false;
        }

        if (!TryReadHeaderValue(headers, BusinessSessionHeaders.ActiveCompanyId, out var companyIdValue))
        {
            error = $"Missing required business session header '{BusinessSessionHeaders.ActiveCompanyId}'.";
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

    private static bool TryReadHeaderValue(IHeaderDictionary headers, string key, out string value)
    {
        value = string.Empty;

        if (!headers.TryGetValue(key, out StringValues values))
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
