namespace SharedKernel.Company;

public sealed record class CompanyBookRecord(
    Guid BookId,
    Guid CompanyId,
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
