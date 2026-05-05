using SharedKernel.Identity;

namespace SharedKernel.Company;

public sealed record class CompanyBookRemeasurementPolicy(
    Guid PolicyId,
    CompanyId CompanyId,
    Guid BookId,
    string RateType,
    string QuoteBasis,
    string RateUseCase,
    string PostingReason,
    string RevaluationProfile,
    string FxRoundingPolicy,
    DateOnly EffectiveFrom,
    bool IsActive);
