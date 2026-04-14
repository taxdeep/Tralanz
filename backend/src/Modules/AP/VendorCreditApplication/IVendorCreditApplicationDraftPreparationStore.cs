namespace Modules.AP.VendorCreditApplication;

public interface IVendorCreditApplicationDraftPreparationStore
{
    Task<IReadOnlyList<VendorCreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid vendorId,
        string documentCurrencyCode,
        CancellationToken cancellationToken);

    Task<VendorCreditApplicationDraftResult> PrepareDraftAsync(
        VendorCreditApplicationDraftPreparation preparation,
        CancellationToken cancellationToken);
}
