namespace Modules.AP.PayBill;

public interface IPayBillDraftPreparationWorkflow
{
    Task<IReadOnlyList<PayBillOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid vendorId,
        CancellationToken cancellationToken);

    Task<PayBillDraftResult> PrepareDraftAsync(
        PayBillDraftContext context,
        IReadOnlyList<PayBillDraftLine> lines,
        CancellationToken cancellationToken);
}
