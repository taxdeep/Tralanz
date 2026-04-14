using System.Net.Http.Json;

namespace Web.Business.AP.SettlementPosting;

public sealed class ApSettlementPostingClient(HttpClient httpClient)
{
    public Task<ApSettlementPostingResult> PostPayBillAsync(
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

    public Task<ApSettlementPostingResult> PostVendorCreditApplicationAsync(
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

    private async Task<ApSettlementPostingResult> PostAsync(string requestUri, object payload, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync<ApSettlementPostingResult>(cancellationToken))!;
        }

        var error = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
        throw new InvalidOperationException(error?.Message ?? $"Posting failed with HTTP {(int)response.StatusCode}.");
    }

    private sealed record class ErrorPayload(string? Message);
}
