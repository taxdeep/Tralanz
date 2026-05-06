using SharedKernel.Identity;

namespace SharedKernel.Company;

public sealed record class CompanyBookRecord(
    Guid BookId,
    CompanyId CompanyId,
    string BookCode,
    string BookName,
    string BookRole,
    string AccountingStandard,
    string BookBaseCurrencyCode,
    string FunctionalCurrencyCode,
    string? PresentationCurrencyCode,
    bool IsPrimary,
    bool IsAdjustmentOnly,
    DateOnly EffectiveFrom,
    bool IsActive);
