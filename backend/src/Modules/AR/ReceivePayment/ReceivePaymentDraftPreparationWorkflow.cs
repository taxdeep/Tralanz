using Engines.FX.FxRateLookup;
using Modules.AR.CustomerCurrency;
using Modules.Company.MultiCurrency;
using SharedKernel.FX;

namespace Modules.AR.ReceivePayment;

public sealed class ReceivePaymentDraftPreparationWorkflow : IReceivePaymentDraftPreparationWorkflow
{
    private readonly IReceivePaymentDraftPreparationStore _store;
    private readonly ICustomerCurrencyWorkflow _customerCurrencyWorkflow;
    private readonly ICompanyCurrencyCatalog _companyCurrencyCatalog;
    private readonly IFxRateResolver _fxRateResolver;
    private readonly IFxRateStore _fxRateStore;

    public ReceivePaymentDraftPreparationWorkflow(
        IReceivePaymentDraftPreparationStore store,
        ICustomerCurrencyWorkflow customerCurrencyWorkflow,
        ICompanyCurrencyCatalog companyCurrencyCatalog,
        IFxRateResolver fxRateResolver,
        IFxRateStore fxRateStore)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _customerCurrencyWorkflow = customerCurrencyWorkflow ?? throw new ArgumentNullException(nameof(customerCurrencyWorkflow));
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
        _fxRateResolver = fxRateResolver ?? throw new ArgumentNullException(nameof(fxRateResolver));
        _fxRateStore = fxRateStore ?? throw new ArgumentNullException(nameof(fxRateStore));
    }

    public async Task<IReadOnlyList<ReceivePaymentOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var preference = await _customerCurrencyWorkflow.GetPreferenceAsync(customerId, cancellationToken);
        if (preference.CompanyId != companyId)
        {
            throw new InvalidOperationException("Customer does not belong to the active company.");
        }

        var profile = await _companyCurrencyCatalog.GetProfileAsync(companyId, cancellationToken);
        if (!profile.IsCurrencyEnabled(preference.DefaultCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Customer currency {preference.DefaultCurrencyCode} is not enabled for company {companyId:D}.");
        }

        return await _store.ListOpenItemCandidatesAsync(
            companyId,
            customerId,
            preference.DefaultCurrencyCode,
            cancellationToken);
    }

    public async Task<ReceivePaymentDraftResult> PrepareDraftAsync(
        ReceivePaymentDraftContext context,
        IReadOnlyList<ReceivePaymentDraftLine> lines,
        CancellationToken cancellationToken)
    {
        var preference = await _customerCurrencyWorkflow.GetPreferenceAsync(context.CustomerId, cancellationToken);
        if (preference.CompanyId != context.CompanyId)
        {
            throw new InvalidOperationException("Customer does not belong to the active company.");
        }

        var documentCurrencyCode = NormalizeCurrencyCode(preference.DefaultCurrencyCode);
        if (!string.IsNullOrWhiteSpace(context.RequestedCurrencyCode))
        {
            var requested = NormalizeCurrencyCode(context.RequestedCurrencyCode);
            if (!string.Equals(requested, documentCurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Customer {preference.DisplayName} is locked to {documentCurrencyCode}, so receive payments cannot use {requested}.");
            }
        }

        var companyProfile = await _companyCurrencyCatalog.GetProfileAsync(context.CompanyId, cancellationToken);
        if (!companyProfile.IsCurrencyEnabled(documentCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Customer currency {documentCurrencyCode} is not enabled for company {context.CompanyId:D}.");
        }

        await EnsurePhaseOneSameCurrencySettlementAsync(
            context.CompanyId,
            context.CustomerId,
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

        var preparation = new ReceivePaymentDraftPreparation(
            context,
            documentCurrencyCode,
            baseCurrencyCode,
            fxResolution,
            lines);

        return await _store.PrepareDraftAsync(preparation, cancellationToken);
    }

    private async Task EnsurePhaseOneSameCurrencySettlementAsync(
        Guid companyId,
        Guid customerId,
        string documentCurrencyCode,
        IReadOnlyList<ReceivePaymentDraftLine> lines,
        CancellationToken cancellationToken)
    {
        var requestedIds = lines
            .Select(static line => line.TargetOpenItemId)
            .Distinct()
            .ToHashSet();

        var allowedCandidates = await _store.ListOpenItemCandidatesAsync(
            companyId,
            customerId,
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
            $"Phase 1 receive payment only supports same-currency settlement. Target open items must stay open in {documentCurrencyCode}.");
    }

    private async Task<FxRateResolution> ResolveFxAsync(
        Guid companyId,
        Guid userId,
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
