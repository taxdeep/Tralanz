namespace Modules.AR.ReceivePayment;

public interface IReceivePaymentDraftPreparationStore
{
    Task<IReadOnlyList<ReceivePaymentOpenItemCandidate>> ListOpenItemCandidatesAsync(
        CompanyId companyId,
        Guid customerId,
        string documentCurrencyCode,
        CancellationToken cancellationToken);

    Task<ReceivePaymentDraftResult> PrepareDraftAsync(
        ReceivePaymentDraftPreparation preparation,
        CancellationToken cancellationToken);
}
