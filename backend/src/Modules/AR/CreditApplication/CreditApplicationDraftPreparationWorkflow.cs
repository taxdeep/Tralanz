using Modules.AR.CustomerCurrency;
using Modules.Company.MultiCurrency;

namespace Modules.AR.CreditApplication;

public sealed class CreditApplicationDraftPreparationWorkflow : ICreditApplicationDraftPreparationWorkflow
{
    private readonly ICreditApplicationDraftPreparationStore _store;
    private readonly ICustomerCurrencyWorkflow _customerCurrencyWorkflow;
    private readonly ICompanyCurrencyCatalog _companyCurrencyCatalog;

    public CreditApplicationDraftPreparationWorkflow(
        ICreditApplicationDraftPreparationStore store,
        ICustomerCurrencyWorkflow customerCurrencyWorkflow,
        ICompanyCurrencyCatalog companyCurrencyCatalog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _customerCurrencyWorkflow = customerCurrencyWorkflow ?? throw new ArgumentNullException(nameof(customerCurrencyWorkflow));
        _companyCurrencyCatalog = companyCurrencyCatalog ?? throw new ArgumentNullException(nameof(companyCurrencyCatalog));
    }

    public async Task<IReadOnlyList<CreditApplicationOpenItemCandidate>> ListOpenItemCandidatesAsync(
        CompanyId companyId,
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

    public async Task<CreditApplicationDraftResult> PrepareDraftAsync(
        CreditApplicationDraftContext context,
        IReadOnlyList<CreditApplicationDraftLine> lines,
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
                    $"Customer {preference.DisplayName} is locked to {documentCurrencyCode}, so credit applications cannot use {requested}.");
            }
        }

        var companyProfile = await _companyCurrencyCatalog.GetProfileAsync(context.CompanyId, cancellationToken);
        if (!companyProfile.IsCurrencyEnabled(documentCurrencyCode))
        {
            throw new InvalidOperationException(
                $"Customer currency {documentCurrencyCode} is not enabled for company {context.CompanyId:D}.");
        }

        await EnsurePhaseOneSameCurrencyApplicationAsync(
            context.CompanyId,
            context.CustomerId,
            documentCurrencyCode,
            lines,
            cancellationToken);

        var preparation = new CreditApplicationDraftPreparation(
            context,
            documentCurrencyCode,
            companyProfile.BaseCurrencyCode,
            lines);

        return await _store.PrepareDraftAsync(preparation, cancellationToken);
    }

    private async Task EnsurePhaseOneSameCurrencyApplicationAsync(
        CompanyId companyId,
        Guid customerId,
        string documentCurrencyCode,
        IReadOnlyList<CreditApplicationDraftLine> lines,
        CancellationToken cancellationToken)
    {
        var requestedIds = lines
            .SelectMany(static line => new[] { line.SourceCreditOpenItemId, line.TargetInvoiceOpenItemId })
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
            $"Phase 1 credit application only supports same-currency application. Source and target open items must stay open in {documentCurrencyCode}.");
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
