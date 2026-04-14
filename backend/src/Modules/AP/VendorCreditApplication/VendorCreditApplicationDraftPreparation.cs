namespace Modules.AP.VendorCreditApplication;

public sealed record VendorCreditApplicationDraftPreparation(
    VendorCreditApplicationDraftContext Context,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    IReadOnlyList<VendorCreditApplicationDraftLine> Lines);
