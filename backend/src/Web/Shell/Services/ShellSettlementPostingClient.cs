using System.Net.Http.Json;

namespace Web.Shell.Services;

public sealed class ShellSettlementPostingClient(HttpClient httpClient, ILogger<ShellSettlementPostingClient> logger)
{
    public Task<ShellSettlementPostingResult?> PostReceivePaymentAsync(
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

    public Task<ShellSettlementPostingResult?> PostPayBillAsync(
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

    public Task<ShellSettlementPostingResult?> PostCreditApplicationAsync(
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

    public Task<ShellSettlementPostingResult?> PostVendorCreditApplicationAsync(
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

    private async Task<ShellSettlementPostingResult?> PostAsync(
        string requestUri,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ShellSettlementPostingResult>(cancellationToken);
            }

            var error = await ReadErrorMessageAsync(response, cancellationToken);
            throw new InvalidOperationException(error);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            logger.LogWarning(ex, "Unable to post settlement document using request {RequestUri}.", requestUri);
            throw new InvalidOperationException("Posting failed because the settlement request could not be completed.", ex);
        }
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ShellErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload.Message;
            }
        }
        catch
        {
        }

        return $"Posting failed with HTTP {(int)response.StatusCode}.";
    }

    private sealed record class ShellErrorPayload(string? Message);
}
