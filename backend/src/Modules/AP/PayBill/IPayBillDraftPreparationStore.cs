namespace Modules.AP.PayBill;

public interface IPayBillDraftPreparationStore
{
    Task<IReadOnlyList<PayBillOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid vendorId,
        string documentCurrencyCode,
        CancellationToken cancellationToken);

    Task<PayBillDraftResult> PrepareDraftAsync(
        PayBillDraftPreparation preparation,
        CancellationToken cancellationToken);
}
