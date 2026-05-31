// SalesTaxEngine — v2 compute pipeline.
//
// Pure compute: takes a request + an ISalesTaxCatalogReader, returns
// per-line per-component snapshot drafts. Single-component is the
// common case; the implementation handles 1..N components per code
// with the compound-base rule.
//
// Designed to be called from inside the host repository's SaveDraftAsync.
// The companion ITaxSnapshotPersister writes the resulting snapshots
// into document_line_sales_tax_snapshots within the same save flow.
//
// ROUNDING: rate × amount intermediate kept at decimal(18,6); final
// tax_amount rounds to currency.minor_unit (hardcoded to 2 in S2.0 —
// currency-aware rounding is a later refinement). Banker's rounding
// (ToEven) matches the .NET default and avoids systematic bias across
// many small transactions.

using Citus.Modules.SalesTax.Application.Contracts;
using Citus.Modules.SalesTax.Domain.Shared;

namespace Citus.Modules.SalesTax.Application;

public sealed class SalesTaxEngine : ISalesTaxEngine
{
    private const int CurrencyMinorUnit = 2;
    private const MidpointRounding RoundingMode = MidpointRounding.ToEven;

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<TaxCatalogComponentRow>> EmptyRows
        = new Dictionary<Guid, IReadOnlyList<TaxCatalogComponentRow>>();

    private readonly ISalesTaxCatalogReader _catalog;

    public SalesTaxEngine(ISalesTaxCatalogReader catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<SalesTaxComputationResult> ComputeAsync(
        SalesTaxComputationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count == 0)
        {
            return new SalesTaxComputationResult(Array.Empty<SalesTaxLineResult>());
        }

        // Redesign (R3): a line may select a Tax Code bundle (TaxCodeSetId)
        // or, on the legacy path, a single Rule (LegacyTaxCodeId). A bundle
        // takes precedence; the legacy path is left untouched so live
        // documents carrying a single Rule id compute exactly as before.
        var distinctSetIds = request.Lines
            .Where(l => l.TaxCodeSetId.HasValue)
            .Select(l => l.TaxCodeSetId!.Value)
            .Distinct()
            .ToArray();

        var distinctLegacyIds = request.Lines
            .Where(l => !l.TaxCodeSetId.HasValue && l.LegacyTaxCodeId.HasValue)
            .Select(l => l.LegacyTaxCodeId!.Value)
            .Distinct()
            .ToArray();

        if (distinctSetIds.Length == 0 && distinctLegacyIds.Length == 0)
        {
            return new SalesTaxComputationResult(
                request.Lines
                    .Select(l => new SalesTaxLineResult(
                        l.LineId, 0m, 0m, 0m, Array.Empty<TaxSnapshotDraft>()))
                    .ToArray());
        }

        var rulesBySetId = distinctSetIds.Length > 0
            ? await _catalog.GetRulesForTaxCodeSetsAsync(
                request.CompanyId, distinctSetIds, request.TaxPointDate, cancellationToken)
            : EmptyRows;

        var componentsByLegacyId = distinctLegacyIds.Length > 0
            ? await _catalog.GetComponentsForLegacyIdsAsync(
                request.CompanyId, distinctLegacyIds, request.TaxPointDate, cancellationToken)
            : EmptyRows;

        var resultLines = new List<SalesTaxLineResult>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            IReadOnlyList<TaxCatalogComponentRow>? rows = null;
            if (line.TaxCodeSetId.HasValue)
            {
                rulesBySetId.TryGetValue(line.TaxCodeSetId.Value, out rows);
            }
            else if (line.LegacyTaxCodeId.HasValue)
            {
                componentsByLegacyId.TryGetValue(line.LegacyTaxCodeId.Value, out rows);
            }

            if (rows is null || rows.Count == 0)
            {
                resultLines.Add(new SalesTaxLineResult(
                    line.LineId, 0m, 0m, 0m, Array.Empty<TaxSnapshotDraft>()));
                continue;
            }

            resultLines.Add(ComputeLine(line, rows, request));
        }

        return new SalesTaxComputationResult(resultLines);
    }

    private static SalesTaxLineResult ComputeLine(
        SalesTaxLineRequest line,
        IReadOnlyList<TaxCatalogComponentRow> components,
        SalesTaxComputationRequest request)
    {
        var snapshots = new List<TaxSnapshotDraft>(components.Count);
        var totalTax = 0m;
        var totalRecoverable = 0m;
        var totalNonRecoverable = 0m;

        var taxableAmountBase = Math.Abs(line.LineAmount);
        var compoundAccrual = 0m;

        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];

            var componentBase = component.IsCompound
                ? taxableAmountBase + compoundAccrual
                : taxableAmountBase;

            var taxRaw = componentBase * component.RatePercent / 100m;
            var taxAmount = Math.Round(taxRaw, CurrencyMinorUnit, RoundingMode);
            if (line.LineAmount < 0m) taxAmount = -taxAmount;

            compoundAccrual += taxAmount;

            var (recoverable, nonRecoverable) = SplitRecoverability(
                request.DocumentSide,
                taxAmount,
                component.RecoverabilityMode,
                component.RecoverablePercent);

            var taxAmountBase = Math.Round(
                taxAmount * request.FxRateToBase, CurrencyMinorUnit, RoundingMode);

            snapshots.Add(new TaxSnapshotDraft(
                Sequence: i + 1,
                Leg: TaxSnapshotLeg.Primary,
                TaxCodeId: component.TaxCodeId,
                ComponentId: component.ComponentId,
                JurisdictionId: component.JurisdictionId,
                CodeSnapshot: component.Code,
                NameSnapshot: component.Name,
                RegimeTypeSnapshot: component.RegimeType,
                TreatmentSnapshot: component.Treatment,
                RatePercentSnapshot: component.RatePercent,
                IsCompoundSnapshot: component.IsCompound,
                ReportingBoxCodes: component.BoxCodes,
                TaxableAmount: line.LineAmount,
                TaxAmount: taxAmount,
                RecoverableAmount: recoverable,
                NonRecoverableAmount: nonRecoverable,
                DocumentCurrencyCode: request.DocumentCurrencyCode,
                TaxAmountBase: taxAmountBase,
                FxRateSnapshot: request.FxRateToBase,
                PayableAccountId: component.PayableAccountId,
                RecoverableAccountId: component.RecoverableAccountId,
                NonRecoverableAccountId: component.NonRecoverableAccountId));

            totalTax += taxAmount;
            totalRecoverable += recoverable;
            totalNonRecoverable += nonRecoverable;
        }

        return new SalesTaxLineResult(
            line.LineId, totalTax, totalRecoverable, totalNonRecoverable, snapshots);
    }

    private static (decimal Recoverable, decimal NonRecoverable) SplitRecoverability(
        SalesTaxDocumentSide side,
        decimal taxAmount,
        string mode,
        decimal? recoverablePercent)
    {
        if (side == SalesTaxDocumentSide.Sales)
        {
            return (0m, 0m);
        }

        return mode switch
        {
            TaxRecoverabilityMode.Full => (taxAmount, 0m),
            TaxRecoverabilityMode.None => (0m, taxAmount),
            TaxRecoverabilityMode.Partial when recoverablePercent.HasValue
                => SplitPartial(taxAmount, recoverablePercent.Value),
            _ => (taxAmount, 0m),
        };
    }

    private static (decimal Recoverable, decimal NonRecoverable) SplitPartial(
        decimal taxAmount, decimal percent)
    {
        var recoverable = Math.Round(
            taxAmount * percent / 100m,
            CurrencyMinorUnit,
            RoundingMode);
        return (recoverable, taxAmount - recoverable);
    }
}
