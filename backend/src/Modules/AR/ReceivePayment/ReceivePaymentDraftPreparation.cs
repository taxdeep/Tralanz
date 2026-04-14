using SharedKernel.FX;

namespace Modules.AR.ReceivePayment;

public sealed record ReceivePaymentDraftPreparation(
    ReceivePaymentDraftContext Context,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    FxRateResolution FxResolution,
    IReadOnlyList<ReceivePaymentDraftLine> Lines);
