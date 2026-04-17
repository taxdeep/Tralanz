using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellOpenItemAdjustmentAccountMappingClient(
    HttpClient httpClient,
    ILogger<ShellOpenItemAdjustmentAccountMappingClient> logger)
{
    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingListResponse>> ListAsync(
        Guid companyId,
        string? openItemType,
        string? adjustmentType,
        bool includeInactive,
        Guid? bookId,
        string? policyScope,
        string? searchText,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var requestUri =
            $"accounting/open-item-adjustment-account-mappings?companyId={companyId:D}&includeInactive={includeInactive.ToString().ToLowerInvariant()}&limit={Math.Clamp(limit, 1, 500)}";

        if (!string.IsNullOrWhiteSpace(openItemType))
        {
            requestUri += $"&openItemType={Uri.EscapeDataString(openItemType)}";
        }

        if (!string.IsNullOrWhiteSpace(adjustmentType))
        {
            requestUri += $"&adjustmentType={Uri.EscapeDataString(adjustmentType)}";
        }

        if (bookId.HasValue)
        {
            requestUri += $"&bookId={bookId.Value:D}";
        }

        if (!string.IsNullOrWhiteSpace(policyScope))
        {
            requestUri += $"&policyScope={Uri.EscapeDataString(policyScope)}";
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            requestUri += $"&searchText={Uri.EscapeDataString(searchText)}";
        }

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingListResponse>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentAccountMappingListResponse>(cancellationToken);
                return result is null
                    ? WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingListResponse>.Failure(
                        "Mapping list request succeeded but returned an empty payload.")
                    : WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingListResponse>.Success(result);
            }

            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingListResponse>.Failure(
                await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load open-item adjustment account mappings for company {CompanyId}.", companyId);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingListResponse>.Failure(
                "Unable to load mappings. Check Accounting API availability and business-session headers.");
        }
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingSaveResult>> SaveAsync(
        ShellOpenItemAdjustmentAccountMappingSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "accounting/open-item-adjustment-account-mappings",
                request,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingSaveResult>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentAccountMappingSaveResult>(cancellationToken);
                return result is null
                    ? WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingSaveResult>.Failure(
                        "Mapping save succeeded but returned an empty payload.")
                    : WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingSaveResult>.Success(result);
            }

            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingSaveResult>.Failure(
                await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unable to save open-item adjustment account mapping for company {CompanyId}.",
                request.CompanyId);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingSaveResult>.Failure(
                "Unable to save the mapping. Check Accounting API availability and business-session headers.");
        }
    }

    public async Task<WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingTransitionResult>> DeactivateAsync(
        Guid companyId,
        Guid? userId,
        Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            CompanyId = companyId,
            UserId = userId
        };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"accounting/open-item-adjustment-account-mappings/{mappingId:D}/deactivate",
                request,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingTransitionResult>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentAccountMappingTransitionResult>(cancellationToken);
                return result is null
                    ? WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingTransitionResult>.Failure(
                        "Mapping deactivation succeeded but returned an empty payload.")
                    : WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingTransitionResult>.Success(result);
            }

            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingTransitionResult>.Failure(
                await ReadErrorAsync(response, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unable to deactivate open-item adjustment account mapping {MappingId} for company {CompanyId}.",
                mappingId,
                companyId);
            return WebShellAuthenticatedApiResult<ShellOpenItemAdjustmentAccountMappingTransitionResult>.Failure(
                "Unable to deactivate the mapping. Check Accounting API availability and business-session headers.");
        }
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var error = await response.Content.ReadFromJsonAsync<ShellOpenItemAdjustmentAccountMappingError>(cancellationToken);
        if (!string.IsNullOrWhiteSpace(error?.Message))
        {
            return error.Message;
        }

        return $"Open-item adjustment account mapping request returned HTTP {(int)response.StatusCode}.";
    }
}
