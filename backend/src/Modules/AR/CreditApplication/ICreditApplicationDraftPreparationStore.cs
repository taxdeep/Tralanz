namespace Modules.AR.CreditApplication;

public interface ICreditApplicationDraftPreparationStore
{
    Task<IReadOnlyList<CreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        CompanyId companyId,
        Guid customerId,
        string documentCurrencyCode,
        CancellationToken cancellationToken);

    Task<CreditApplicationDraftResult> PrepareDraftAsync(
        CreditApplicationDraftPreparation preparation,
        CancellationToken cancellationToken);
}
