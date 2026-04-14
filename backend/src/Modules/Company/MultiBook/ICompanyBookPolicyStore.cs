using SharedKernel.Company;

namespace Modules.Company.MultiBook;

public interface ICompanyBookPolicyStore
{
    Task<IReadOnlyList<CompanyBookGovernanceState>> ListBookGovernanceAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
        Guid companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalRecord> CreateGovernanceSignalAsync(
        Guid companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> CreateGovernedChangeRequestDraftAsync(
        CompanyBookGovernedChangePreview preview,
        DateOnly asOfDate,
        DateOnly effectiveFrom,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft?> GetGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> SubmitGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> CancelGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult?> TryGetDefaultRemeasurementPolicyAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult?> TryGetRemeasurementPolicyAsync(
        Guid companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
        Guid companyId,
        Guid userId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);
}
