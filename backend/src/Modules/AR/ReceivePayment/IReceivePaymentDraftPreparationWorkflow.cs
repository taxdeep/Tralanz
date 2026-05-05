namespace Modules.AR.ReceivePayment;

public interface IReceivePaymentDraftPreparationWorkflow
{
    Task<IReadOnlyList<ReceivePaymentOpenItemCandidate>> ListOpenItemCandidatesAsync(
        CompanyId companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<ReceivePaymentDraftResult> PrepareDraftAsync(
        ReceivePaymentDraftContext context,
        IReadOnlyList<ReceivePaymentDraftLine> lines,
        CancellationToken cancellationToken);
}
