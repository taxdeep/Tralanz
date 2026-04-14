namespace Modules.Company.MultiBook;

public interface ICompanyBookPolicyWorkflow
{
    Task<IReadOnlyList<CompanyBookGovernanceOverview>> ListBookGovernanceAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
        Guid companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> CreateGovernanceSignalAsync(
        Guid companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> RegisterClosedPeriodAsync(
        Guid companyId,
        Guid bookId,
        DateOnly periodEndDate,
        string? referenceLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> RegisterIssuedStatementAsync(
        Guid companyId,
        Guid bookId,
        DateOnly issuedOn,
        string statementLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> RegisterFiledTaxAsync(
        Guid companyId,
        Guid bookId,
        DateOnly filedOn,
        string filingLabel,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> PrepareGovernedChangeRequestDraftAsync(
        Guid companyId,
        Guid userId,
        Guid? bookId,
        DateOnly asOfDate,
        DateOnly effectiveFrom,
        CompanyBookProposedChangeSet proposedChanges,
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

    Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
        Guid companyId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestReadiness> ValidateGovernedChangeRequestApplyReadinessAsync(
        Guid companyId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangePreview> PreviewGovernedChangeAsync(
        Guid companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CompanyBookProposedChangeSet proposedChanges,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> GetRemeasurementPolicyAsync(
        Guid companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> GetDefaultRemeasurementPolicyAsync(
        Guid companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
        Guid companyId,
        Guid userId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);
}
