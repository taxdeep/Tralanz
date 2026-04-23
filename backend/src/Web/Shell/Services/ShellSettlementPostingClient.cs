using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellSettlementPostingClient(HttpClient httpClient, ILogger<ShellSettlementPostingClient> logger)
{
    public Task<WebShellAuthenticatedApiResult<ShellSettlementPostingResult>> PostReceivePaymentAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/receive-payments/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"receive-payment:{documentId:D}"
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSettlementPostingResult>> PostPayBillAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/pay-bills/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"pay-bill:{documentId:D}"
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSettlementPostingResult>> PostCreditApplicationAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/credit-applications/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                IdempotencyKey = $"credit-application:{documentId:D}"
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSettlementPostingResult>> PostVendorCreditApplicationAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/vendor-credit-applications/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                IdempotencyKey = $"vendor-credit-application:{documentId:D}"
            },
            cancellationToken);

    private async Task<WebShellAuthenticatedApiResult<ShellSettlementPostingResult>> PostAsync(
        string requestUri,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellSettlementPostingResult>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ShellSettlementPostingResult>(cancellationToken);
                return result is null
                    ? WebShellAuthenticatedApiResult<ShellSettlementPostingResult>.Failure("Posting succeeded but no settlement result was returned.")
                    : WebShellAuthenticatedApiResult<ShellSettlementPostingResult>.Success(result);
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            return WebShellAuthenticatedApiResult<ShellSettlementPostingResult>.Failure(error.Message, error.Code);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to post settlement document using request {RequestUri}.", requestUri);
            return WebShellAuthenticatedApiResult<ShellSettlementPostingResult>.Failure("Posting failed because the settlement request could not be completed.");
        }
    }

    private static async Task<ShellErrorPayload> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ShellErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload;
            }
        }
        catch
        {
        }

        return new ShellErrorPayload(null, $"Posting failed with HTTP {(int)response.StatusCode}.");
    }

    private sealed record class ShellErrorPayload(string? Code, string Message);
}
