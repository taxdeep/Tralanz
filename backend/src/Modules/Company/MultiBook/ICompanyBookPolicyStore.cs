using SharedKernel.Company;

namespace Modules.Company.MultiBook;

public interface ICompanyBookPolicyStore
{
    Task<IReadOnlyList<CompanyBookGovernanceState>> ListBookGovernanceAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalRecord> CreateGovernanceSignalAsync(
        CompanyId companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> CreateGovernedChangeRequestDraftAsync(
        CompanyBookGovernedChangePreview preview,
        DateOnly asOfDate,
        DateOnly effectiveFrom,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft?> GetGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> SubmitGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        Guid requestId,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> CancelGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        Guid requestId,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult?> TryGetDefaultRemeasurementPolicyAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult?> TryGetRemeasurementPolicyAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
        CompanyId companyId,
        UserId userId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);
}
