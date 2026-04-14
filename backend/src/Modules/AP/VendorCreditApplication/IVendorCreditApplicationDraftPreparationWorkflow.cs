namespace Modules.AP.VendorCreditApplication;

public interface IVendorCreditApplicationDraftPreparationWorkflow
{
    Task<IReadOnlyList<VendorCreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<VendorCreditApplicationDraftResult> PrepareDraftAsync(
        VendorCreditApplicationDraftContext context,
        IReadOnlyList<VendorCreditApplicationDraftLine> lines,
        CancellationToken cancellationToken);
}
