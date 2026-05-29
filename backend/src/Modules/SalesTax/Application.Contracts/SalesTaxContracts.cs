// Sales Tax v2 Application contracts.
//
// Two interfaces matter for S2:
//   * ISalesTaxEngine — given a request (company, document date,
//     currency, lines with {amount, tax_code_id, side}), returns per-
//     line tax components ready to persist + write into the line's
//     tax_amount aggregate.
//   * ITaxSnapshotPersister — writes the engine's output into
//     document_line_sales_tax_snapshots. Idempotent on the natural
//     key so re-saves (e.g. successive Save Draft clicks) replace the
//     previous snapshot rows rather than duplicating.
//
// Both are designed to be called from inside the existing per-document
// repositories' SaveDraftAsync methods (PostgresInvoiceDocumentRepository,
// PostgresBillDocumentRepository, …). S2.1+ wires them in.

using Citus.Modules.SalesTax.Domain.Shared;

namespace Citus.Modules.SalesTax.Application.Contracts;

public interface ISalesTaxEngine
{
    /// <summary>
    /// Compute per-line per-component tax for a document save. The
    /// returned <see cref="SalesTaxComputationResult"/> carries enough
    /// detail to (a) update the line's denormalized <c>tax_amount</c>
    /// and (b) hand off to <see cref="ITaxSnapshotPersister"/> for
    /// snapshot writes.
    ///
    /// Idempotent: same input on a draft re-save yields the same output.
    /// </summary>
    Task<SalesTaxComputationResult> ComputeAsync(
        SalesTaxComputationRequest request,
        CancellationToken cancellationToken);
}

public sealed record SalesTaxComputationRequest(
    string CompanyId,
    DateOnly TaxPointDate,
    string DocumentCurrencyCode,
    SalesTaxDocumentSide DocumentSide,
    IReadOnlyList<SalesTaxLineRequest> Lines,
    decimal FxRateToBase = 1m);

public sealed record SalesTaxLineRequest(
    Guid LineId,
    decimal LineAmount,
    // Legacy tax_codes.id — engine resolves to v2 sales_tax_codes via
    // the legacy_tax_code_id back-reference written by S1.3. Null when
    // the operator skipped tax (engine returns an empty Snapshots list).
    Guid? LegacyTaxCodeId);

public sealed record SalesTaxComputationResult(
    IReadOnlyList<SalesTaxLineResult> Lines)
{
    public decimal TotalTaxAmount => Lines.Sum(l => l.TotalTaxAmount);
}

public sealed record SalesTaxLineResult(
    Guid LineId,
    decimal TotalTaxAmount,
    decimal TotalRecoverableAmount,
    decimal TotalNonRecoverableAmount,
    IReadOnlyList<TaxSnapshotDraft> Snapshots);

/// <summary>
/// One row's worth of snapshot data, pre-persist. Sequence numbers
/// start at 1 within a single line.
/// </summary>
public sealed record TaxSnapshotDraft(
    int Sequence,
    string Leg,
    Guid TaxCodeId,
    Guid ComponentId,
    Guid JurisdictionId,
    string CodeSnapshot,
    string NameSnapshot,
    string RegimeTypeSnapshot,
    string TreatmentSnapshot,
    decimal RatePercentSnapshot,
    bool IsCompoundSnapshot,
    IReadOnlyList<string> ReportingBoxCodes,
    decimal TaxableAmount,
    decimal TaxAmount,
    decimal RecoverableAmount,
    decimal NonRecoverableAmount,
    string DocumentCurrencyCode,
    decimal TaxAmountBase,
    decimal FxRateSnapshot);

public interface ITaxSnapshotPersister
{
    /// <summary>
    /// Write snapshot rows for a saved document line set. Replaces any
    /// existing rows for the same (documentType, documentId) — call sites
    /// pass the engine's full result, the persister handles
    /// upsert-by-natural-key.
    /// </summary>
    Task PersistAsync(
        string companyId,
        string documentType,
        Guid documentId,
        IReadOnlyList<(Guid LineId, SalesTaxLineResult Result)> lineResults,
        CancellationToken cancellationToken);
}

/// <summary>
/// Catalog reader the engine consults to resolve legacy tax_code_ids
/// to v2 components, rates, and box mappings. Sits in
/// Application.Contracts so the engine stays free of infrastructure
/// concerns; PostgreSQL implementation lives in
/// Infrastructure/PostgreSQL/SalesTax/.
/// </summary>
public interface ISalesTaxCatalogReader
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<TaxCatalogComponentRow>>> GetComponentsForLegacyIdsAsync(
        string companyId,
        IReadOnlyList<Guid> legacyTaxCodeIds,
        DateOnly asOfDate,
        CancellationToken cancellationToken);
}

public sealed record TaxCatalogComponentRow(
    Guid TaxCodeId,
    string Code,
    string Name,
    string Treatment,
    Guid ComponentId,
    Guid JurisdictionId,
    string RegimeType,
    int Sequence,
    bool IsCompound,
    string RecoverabilityMode,
    decimal? RecoverablePercent,
    decimal RatePercent,
    IReadOnlyList<string> BoxCodes);
