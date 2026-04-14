using Modules.AP.VendorCurrency;
using Modules.Company.MultiCurrency;

namespace Modules.AP.VendorCreditApplication;

public sealed class VendorCreditApplicationDraftPreparationWorkflow : IVendorCreditApplicationDraftPreparationWorkflow
{
    private readonly IVendorCreditApplicationDraftPreparationStore _store;
    private readonly IVendorCurrencyWorkflow _vendorCurrencyWorkflow;
    private readonly ICompanyCurrencyCatalog _companyCurrencyCatalog;

    public VendorCreditApplicationDraftPreparationWorkflow(
        IVendorCreditApplicationDraftPreparationStore store,
        IVendorCurrencyWorkflow vendorCurrencyWorkflow,
        ICompanyCurrencyCatalog companyCurrencyCatalog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vendorCurrencyWorkflow = vendorCurrencyWorkflow ?? throw new ArgumentNullException(nameof(vendorCurrencyWorkflow));
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
    }

    public async Task<IReadOnlyList<VendorCreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        Guid companyId,
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

    public async Task<VendorCreditApplicationDraftResult> PrepareDraftAsync(
        VendorCreditApplicationDraftContext context,
        IReadOnlyList<VendorCreditApplicationDraftLine> lines,
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
                    $"Vendor {preference.DisplayName} is locked to {documentCurrencyCode}, so vendor credit applications cannot use {requested}.");
            }
        }

        var companyProfile = await _companyCurrencyCatalog.GetProfileAsync(context.CompanyId, cancellationToken);
        if (!companyProfile.IsCurrencyEnabled(documentCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Vendor currency {documentCurrencyCode} is not enabled for company {context.CompanyId:D}.");
        }

        await EnsurePhaseOneSameCurrencyApplicationAsync(
            context.CompanyId,
            context.VendorId,
            documentCurrencyCode,
            lines,
            cancellationToken);

        var preparation = new VendorCreditApplicationDraftPreparation(
            context,
            documentCurrencyCode,
            companyProfile.BaseCurrencyCode,
            lines);

        return await _store.PrepareDraftAsync(preparation, cancellationToken);
    }

    private async Task EnsurePhaseOneSameCurrencyApplicationAsync(
        Guid companyId,
        Guid vendorId,
        string documentCurrencyCode,
        IReadOnlyList<VendorCreditApplicationDraftLine> lines,
        CancellationToken cancellationToken)
    {
        var requestedIds = lines
            .SelectMany(static line => new[] { line.SourceVendorCreditOpenItemId, line.TargetBillOpenItemId })
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
            $"Phase 1 vendor credit application only supports same-currency application. Source and target open items must stay open in {documentCurrencyCode}.");
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
