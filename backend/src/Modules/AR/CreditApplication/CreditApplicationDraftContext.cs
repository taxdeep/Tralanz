namespace Modules.AR.CreditApplication;

public sealed record CreditApplicationDraftContext(
    CompanyId CompanyId,
    UserId UserId,
    Guid CustomerId,
    DateOnly ApplicationDate,
    string? RequestedCurrencyCode,
    string? Memo);
