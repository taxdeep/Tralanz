using System.Net.Http.Json;

namespace Web.Business.AR.SettlementPosting;

public sealed class ArSettlementPostingClient(HttpClient httpClient)
{
    public Task<ArSettlementPostingResult> PostReceivePaymentAsync(
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

    public Task<ArSettlementPostingResult> PostCreditApplicationAsync(
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

    private async Task<ArSettlementPostingResult> PostAsync(string requestUri, object payload, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync<ArSettlementPostingResult>(cancellationToken))!;
        }

        var error = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
        throw new InvalidOperationException(error?.Message ?? $"Posting failed with HTTP {(int)response.StatusCode}.");
    }

    private sealed record class ErrorPayload(string? Message);
}
