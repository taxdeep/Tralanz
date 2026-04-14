namespace Modules.AR.CreditApplication;

public interface ICreditApplicationDraftPreparationWorkflow
{
    Task<IReadOnlyList<CreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CreditApplicationDraftResult> PrepareDraftAsync(
        CreditApplicationDraftContext context,
        IReadOnlyList<CreditApplicationDraftLine> lines,
        CancellationToken cancellationToken);
}
