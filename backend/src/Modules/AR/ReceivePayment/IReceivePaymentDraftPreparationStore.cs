namespace Modules.AR.ReceivePayment;

public interface IReceivePaymentDraftPreparationStore
{
    Task<IReadOnlyList<ReceivePaymentOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid customerId,
        string documentCurrencyCode,
        CancellationToken cancellationToken);

    Task<ReceivePaymentDraftResult> PrepareDraftAsync(
        ReceivePaymentDraftPreparation preparation,
        CancellationToken cancellationToken);
}
