namespace Modules.AR.CreditApplication;

public sealed record CreditApplicationDraftPreparation(
    CreditApplicationDraftContext Context,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    IReadOnlyList<CreditApplicationDraftLine> Lines);
