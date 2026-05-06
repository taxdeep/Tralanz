using Engines.FX.FxRateLookup;
using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.FX;

namespace Modules.AP.PayBill;

public sealed class PayBillDraftPreparationWorkflow : IPayBillDraftPreparationWorkflow
{
    private readonly IPayBillDraftPreparationStore _store;
    private readonly IVendorCurrencyWorkflow _vendorCurrencyWorkflow;
    private readonly ICompanyCurrencyCatalog _companyCurrencyCatalog;
    private readonly IFxRateResolver _fxRateResolver;
    private readonly IFxRateStore _fxRateStore;

    public PayBillDraftPreparationWorkflow(
        IPayBillDraftPreparationStore store,
        IVendorCurrencyWorkflow vendorCurrencyWorkflow,
        ICompanyCurrencyCatalog companyCurrencyCatalog,
        IFxRateResolver fxRateResolver,
        IFxRateStore fxRateStore)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vendorCurrencyWorkflow = vendorCurrencyWorkflow ?? throw new ArgumentNullException(nameof(vendorCurrencyWorkflow));
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
        _fxRateResolver = fxRateResolver ?? throw new ArgumentNullException(nameof(fxRateResolver));
        _fxRateStore = fxRateStore ?? throw new ArgumentNullException(nameof(fxRateStore));
    }

    public async Task<IReadOnlyList<PayBillOpenItemCandidate>> ListOpenItemCandidatesAsync(
        CompanyId companyId,
        Guid vendorId,
        CancellationToken cancellationToken)
    {
        var preference = await _vendorCurrencyWorkflow.GetPreferenceAsync(vendorId, cancellationToken);
        if (preference.CompanyId != companyId)
        {
            throw new InvalidOperationException("Vendor does not belong to the active company.");
        }

        var profile = await _companyCurrencyCatalog.GetProfileAsync(companyId, cancellationToken);
        if (!profile.IsCurrencyEnabled(preference.DefaultCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Vendor currency {preference.DefaultCurrencyCode} is not enabled for company {companyId:D}.");
        }

        return await _store.ListOpenItemCandidatesAsync(
            companyId,
            vendorId,
            preference.DefaultCurrencyCode,
            cancellationToken);
    }

    public async Task<PayBillDraftResult> PrepareDraftAsync(
        PayBillDraftContext context,
        IReadOnlyList<PayBillDraftLine> lines,
        CancellationToken cancellationToken)
    {
        var preference = await _vendorCurrencyWorkflow.GetPreferenceAsync(context.VendorId, cancellationToken);
        if (preference.CompanyId != context.CompanyId)
        {
            throw new InvalidOperationException("Vendor does not belong to the active company.");
        }

        var documentCurrencyCode = NormalizeCurrencyCode(preference.DefaultCurrencyCode);
        if (!string.IsNullOrWhiteSpace(context.RequestedCurrencyCode))
        {
            var requested = NormalizeCurrencyCode(context.RequestedCurrencyCode);
            if (!string.Equals(requested, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Vendor {preference.DisplayName} is locked to {documentCurrencyCode}, so pay bills cannot use {requested}.");
            }
        }

        var companyProfile = await _companyCurrencyCatalog.GetProfileAsync(context.CompanyId, cancellationToken);
        if (!companyProfile.IsCurrencyEnabled(documentCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Vendor currency {documentCurrencyCode} is not enabled for company {context.CompanyId:D}.");
        }

        await EnsurePhaseOneSameCurrencySettlementAsync(
            context.CompanyId,
            context.VendorId,
            documentCurrencyCode,
            lines,
            cancellationToken);

        var baseCurrencyCode = companyProfile.BaseCurrencyCode;
        var fxResolution = await ResolveFxAsync(
            context.CompanyId,
            context.UserId,
            baseCurrencyCode,
            documentCurrencyCode,
            context.PaymentDate,
            context.AcceptedFxSnapshotId,
            cancellationToken);

        var preparation = new PayBillDraftPreparation(
            context,
            documentCurrencyCode,
            baseCurrencyCode,
            fxResolution,
            lines);

        return await _store.PrepareDraftAsync(preparation, cancellationToken);
    }

    private async Task EnsurePhaseOneSameCurrencySettlementAsync(
        CompanyId companyId,
        Guid vendorId,
        string documentCurrencyCode,
        IReadOnlyList<PayBillDraftLine> lines,
        CancellationToken cancellationToken)
    {
        var requestedIds = lines
            .Select(static line => line.TargetOpenItemId)
            .Distinct()
            .ToHashSet();

        var allowedCandidates = await _store.ListOpenItemCandidatesAsync(
            companyId,
            vendorId,
            documentCurrencyCode,
            cancellationToken);

        var allowedIds = allowedCandidates
            .Select(static candidate => candidate.OpenItemId)
            .ToHashSet();

        if (requestedIds.All(allowedIds.Contains))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Phase 1 pay bill only supports same-currency settlement. Target open items must stay open in {documentCurrencyCode}.");
    }

    private async Task<FxRateResolution> ResolveFxAsync(
        CompanyId companyId,
        UserId userId,
        string baseCurrencyCode,
        string documentCurrencyCode,
        DateOnly paymentDate,
        Guid? acceptedSnapshotId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(baseCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return FxRateResolution.Identity(paymentDate);
        }

        if (acceptedSnapshotId.HasValue)
        {
            var snapshot = await _fxRateStore.FindCompanySnapshotByIdAsync(
                companyId,
                acceptedSnapshotId.Value,
                cancellationToken);

            if (snapshot is null)
            {
                throw new InvalidOperationException("Accepted FX snapshot could not be found.");
            }

            if (!string.Equals(snapshot.BaseCurrencyCode, baseCurrencyCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(snapshot.QuoteCurrencyCode, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Accepted FX snapshot does not match the payment currency pair.");
            }

            if (snapshot.EffectiveDate > paymentDate)
            {
                throw new InvalidOperationException("Accepted FX snapshot is not valid for the payment date.");
            }

            return new FxRateResolution(
                snapshot.Rate,
                snapshot.RequestedDate,
                snapshot.EffectiveDate,
                snapshot.SnapshotSemantics,
                $"Snapshot {snapshot.Id.ToString("N")[..8]}",
                snapshot.RateType,
                snapshot.QuoteBasis,
                snapshot.RateUseCase,
                snapshot.PostingReason,
                snapshot.ProviderKey,
                snapshot.Id);
        }

        return await _fxRateResolver.ResolveAsync(
            new FxRateLookupRequest(
                companyId,
                userId,
                documentCurrencyCode,
                baseCurrencyCode,
                paymentDate,
                ProviderKey: "frankfurter",
                LookbackDays: 7,
                RateType: FxRateType.Spot,
                QuoteBasis: FxQuoteBasis.Direct,
                RateUseCase: FxRateUseCase.Settlement,
                PostingReason: FxPostingReason.Settlement),
            cancellationToken);
    }

    private static string NormalizeCurrencyCode(string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new InvalidOperationException("A currency code is required.");
        }

        return currencyCode.Trim().ToUpperInvariant();
    }
}
