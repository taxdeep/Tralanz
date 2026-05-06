using SharedKernel.Company;

namespace Modules.Company.MultiBook;

public sealed record class CompanyBookPolicyGovernanceResult(
    CompanyBookRecord Book,
    CompanyBookRemeasurementPolicy RemeasurementPolicy,
    bool WasProvisioned);

public sealed record class CompanyBookGovernanceState(
    CompanyBookRecord Book,
    CompanyBookRemeasurementPolicy? RemeasurementPolicy,
    bool HasCompanyPostedHistory,
    bool HasBookSpecificRevaluationHistory,
    CompanyBookGovernanceSignalSummary GovernanceSignals);

public sealed record class CompanyBookGovernanceOverview(
    CompanyBookRecord Book,
    CompanyBookRemeasurementPolicy? RemeasurementPolicy,
    CompanyBookMigrationEligibility MigrationEligibility,
    CompanyBookGovernanceSignalSummary GovernanceSignals);

public sealed record class CompanyBookProposedChangeSet(
    bool? IsPrimary,
    string? AccountingStandard,
    string? BookBaseCurrencyCode,
    string? FunctionalCurrencyCode,
    string? PresentationCurrencyCode,
    string? RateType,
    string? QuoteBasis,
    string? RateUseCase,
    string? PostingReason,
    string? RevaluationProfile,
    string? FxRoundingPolicy);

public sealed record class CompanyBookGovernedChangePreview(
    CompanyBookRecord Book,
    CompanyBookRemeasurementPolicy? CurrentRemeasurementPolicy,
    CompanyBookProposedChangeSet ProposedChanges,
    CompanyBookChangeImpact ChangeImpact);

public sealed record class CompanyBookGovernedChangeRequestDraft(
    Guid RequestId,
    CompanyId CompanyId,
    Guid BookId,
    string Status,
    string RequestedAction,
    DateOnly AsOfDate,
    DateOnly EffectiveFrom,
    UserId CreatedByUserId,
    DateTimeOffset CreatedAt,
    UserId? SubmittedByUserId,
    DateTimeOffset? SubmittedAt,
    UserId? CancelledByUserId,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? AppliedAt,
    CompanyBookGovernedChangePreview Preview);

public sealed record class CompanyBookGovernedChangeRequestReadiness(
    Guid RequestId,
    string Status,
    DateOnly EffectiveFrom,
    DateOnly EvaluatedAt,
    bool CurrentTruthMatchesDraft,
    bool IsReadyToApply,
    bool RequiresNewBookRollout,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);

public sealed record class CompanyBookGovernanceSignalSummary(
    bool HasClosedPeriods,
    bool HasIssuedReports,
    bool HasFiledTax,
    IReadOnlyList<CompanyBookGovernanceSignalRecord> Signals);

public sealed record class CompanyBookGovernanceSignalWriteResult(
    CompanyBookGovernanceSignalRecord Signal,
    CompanyBookGovernanceSignalSummary Summary);

public sealed record class CompanyBookGovernanceSignalRecord(
    Guid SignalId,
    CompanyId CompanyId,
    Guid BookId,
    string SignalType,
    DateOnly SignalDate,
    string? ReferenceLabel,
    string? Notes,
    UserId? CreatedByUserId,
    DateTimeOffset CreatedAt);

public sealed record class CompanyBookMigrationEligibility(
    string ChangeMode,
    string EvaluationBasis,
    bool HasCompanyPostedHistory,
    bool HasBookSpecificRevaluationHistory,
    bool DirectEditAllowed,
    string Reason);

public sealed record class CompanyBookChangeImpact(
    bool HasAnyChange,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> ChangeCategories,
    bool DirectUpdateAllowed,
    bool GovernedMigrationRequired,
    string RecommendedPath,
    string EvaluationBasis,
    string Reason);
