using SharedKernel.FX;

namespace Modules.AP.PayBill;

public sealed record PayBillDraftPreparation(
    PayBillDraftContext Context,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    FxRateResolution FxResolution,
    IReadOnlyList<PayBillDraftLine> Lines);
