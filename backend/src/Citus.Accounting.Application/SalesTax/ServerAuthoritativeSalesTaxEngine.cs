using Citus.Accounting.Application.Abstractions;
using SharedKernel.Identity;

namespace Citus.Accounting.Application.SalesTax;

public sealed class ServerAuthoritativeSalesTaxEngine(ISalesTaxStore store) : ISalesTaxCalculationEngine
{
    public async Task<SalesTaxPreviewResult> CalculateAsync(
        CompanyId companyId,
        SalesTaxPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!SalesTaxDocumentSide.IsValid(request.DocumentSide))
        {
            throw new InvalidOperationException("Document side must be 'sales' or 'purchase'.");
        }

        if (request.Amount < 0m)
        {
            throw new InvalidOperationException("Tax preview amount must be zero or greater.");
        }

        var codes = await store.ListTaxCodesAsync(companyId, includeInactive: true, cancellationToken).ConfigureAwait(false);
        var code = request.TaxCodeId is { } taxCodeId
            ? codes.FirstOrDefault(c => c.Id == taxCodeId)
            : null;

        if (code is null)
        {
            return new SalesTaxPreviewResult(
                TaxableAmount: request.Amount,
                TaxAmount: 0m,
                RecoverableAmount: 0m,
                NonRecoverableAmount: 0m,
                GrossAmount: request.Amount,
                CurrencyCode: request.CurrencyCode,
                Lines: Array.Empty<SalesTaxPreviewLine>());
        }

        var components = code.Components
            .Where(c => c.AppliesTo is TaxCodeAppliesTo.Both || c.AppliesTo == request.DocumentSide)
            .OrderBy(c => c.Sequence)
            .ToArray();

        if (components.Length == 0)
        {
            return new SalesTaxPreviewResult(
                TaxableAmount: request.Amount,
                TaxAmount: 0m,
                RecoverableAmount: 0m,
                NonRecoverableAmount: 0m,
                GrossAmount: request.Amount,
                CurrencyCode: request.CurrencyCode,
                Lines: Array.Empty<SalesTaxPreviewLine>());
        }

        var isInclusive = string.Equals(request.TaxMode, "inclusive", StringComparison.OrdinalIgnoreCase);
        var nonCompoundRate = components
            .Where(c => c.CompoundMode is "none")
            .Sum(c => c.RatePercent);
        var taxableBase = isInclusive && nonCompoundRate > 0m
            ? Math.Round(request.Amount / (1m + nonCompoundRate / 100m), 6)
            : request.Amount;

        var runningBase = taxableBase;
        var lines = new List<SalesTaxPreviewLine>(components.Length);

        foreach (var component in components)
        {
            var componentBase = component.CompoundMode is "previous_components" ? runningBase : taxableBase;
            var taxAmount = Math.Round(componentBase * component.RatePercent / 100m, 6);
            runningBase += taxAmount;

            var (recoverable, nonRecoverable) = SplitRecoverability(request.DocumentSide, component, taxAmount);
            lines.Add(new SalesTaxPreviewLine(
                TaxCodeId: code.Id,
                TaxComponentId: component.TaxComponentId,
                Code: component.Code,
                Name: component.Name,
                TaxableAmount: taxableBase,
                RatePercent: component.RatePercent,
                TaxAmount: taxAmount,
                RecoverableAmount: recoverable,
                NonRecoverableAmount: nonRecoverable,
                Treatment: component.Treatment,
                Recoverability: component.Recoverability,
                ReportingCategory: ReportingCategory(request.DocumentSide, component)));
        }

        var tax = Math.Round(lines.Sum(l => l.TaxAmount), 6);
        var gross = isInclusive ? request.Amount : Math.Round(taxableBase + tax, 6);
        return new SalesTaxPreviewResult(
            TaxableAmount: taxableBase,
            TaxAmount: tax,
            RecoverableAmount: Math.Round(lines.Sum(l => l.RecoverableAmount), 6),
            NonRecoverableAmount: Math.Round(lines.Sum(l => l.NonRecoverableAmount), 6),
            GrossAmount: gross,
            CurrencyCode: request.CurrencyCode,
            Lines: lines);
    }

    private static (decimal recoverable, decimal nonRecoverable) SplitRecoverability(
        string documentSide,
        SalesTaxCodeComponentRecord component,
        decimal taxAmount)
    {
        if (documentSide == SalesTaxDocumentSide.Sales)
        {
            return (0m, 0m);
        }

        return component.Recoverability switch
        {
            SalesTaxRecoverability.Recoverable => (taxAmount, 0m),
            SalesTaxRecoverability.PartiallyRecoverable => (
                Math.Round(taxAmount * component.RecoverablePercent / 100m, 6),
                Math.Round(taxAmount * (100m - component.RecoverablePercent) / 100m, 6)),
            SalesTaxRecoverability.NonRecoverable => (0m, taxAmount),
            _ => (0m, 0m),
        };
    }

    private static string ReportingCategory(string documentSide, SalesTaxCodeComponentRecord component)
    {
        if (component.Treatment is SalesTaxTreatment.Exempt or SalesTaxTreatment.ZeroRated or SalesTaxTreatment.OutOfScope)
        {
            return component.Treatment;
        }

        return documentSide == SalesTaxDocumentSide.Sales
            ? "tax_collected"
            : component.Recoverability switch
            {
                SalesTaxRecoverability.Recoverable => "input_tax_recoverable",
                SalesTaxRecoverability.PartiallyRecoverable => "input_tax_partially_recoverable",
                SalesTaxRecoverability.NonRecoverable => "non_recoverable_purchase_tax",
                _ => "purchase_tax",
            };
    }
}
