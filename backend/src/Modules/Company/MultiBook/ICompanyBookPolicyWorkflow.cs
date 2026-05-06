namespace Modules.Company.MultiBook;

public interface ICompanyBookPolicyWorkflow
{
    Task<IReadOnlyList<CompanyBookGovernanceOverview>> ListBookGovernanceAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalSummary> GetGovernanceSignalsAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> CreateGovernanceSignalAsync(
        CompanyId companyId,
        Guid bookId,
        string signalType,
        DateOnly signalDate,
        string? referenceLabel,
        string? notes,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> RegisterClosedPeriodAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly periodEndDate,
        string? referenceLabel,
        string? notes,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> RegisterIssuedStatementAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly issuedOn,
        string statementLabel,
        string? notes,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernanceSignalWriteResult> RegisterFiledTaxAsync(
        CompanyId companyId,
        Guid bookId,
        DateOnly filedOn,
        string filingLabel,
        string? notes,
        UserId userId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestDraft> PrepareGovernedChangeRequestDraftAsync(
        CompanyId companyId,
        UserId userId,
        Guid? bookId,
        DateOnly asOfDate,
        DateOnly effectiveFrom,
        CompanyBookProposedChangeSet proposedChanges,
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

    Task<IReadOnlyList<CompanyBookGovernedChangeRequestDraft>> ListGovernedChangeRequestDraftsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangeRequestReadiness> ValidateGovernedChangeRequestApplyReadinessAsync(
        CompanyId companyId,
        Guid requestId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookGovernedChangePreview> PreviewGovernedChangeAsync(
        CompanyId companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CompanyBookProposedChangeSet proposedChanges,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> GetRemeasurementPolicyAsync(
        CompanyId companyId,
        Guid? bookId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> GetDefaultRemeasurementPolicyAsync(
        CompanyId companyId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<CompanyBookPolicyGovernanceResult> EnsureDefaultPrimaryBookPolicyAsync(
        CompanyId companyId,
        UserId userId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);
}
