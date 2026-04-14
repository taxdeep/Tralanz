namespace SharedKernel.Company;

public sealed record class CompanyBookRemeasurementPolicy(
    Guid PolicyId,
    Guid CompanyId,
    Guid BookId,
    string RateType,
    string QuoteBasis,
    string RateUseCase,
    string PostingReason,
    string RevaluationProfile,
    string FxRoundingPolicy,
    DateOnly EffectiveFrom,
    bool IsActive);
