namespace Modules.AP.Bills;

/// <summary>
/// Per-company bill (vendor invoice) draft + lifecycle store. Wraps
/// the existing <c>bills</c> + <c>bill_lines</c> tables from the
/// migration draft, exposing a thin CRUD surface for the Tralanz
/// Books Bill page. The full posting pipeline (FX snapshot, AP open
/// items, journal-entry writes) lives in
/// <c>PostgresBillDocumentRepository</c> and
/// <c>PostBillCommandHandler</c>; this store is the document-level
/// layer that drives the user-facing Draft → Posted → Voided
/// state machine. V1 wiring only flips the status flag — full GL
/// integration is scheduled to land alongside the PO + Inventory
/// batch.
///
/// Lifecycle:
///   Draft   → user can edit; no GL impact, no AP open item
///   Posted  → frozen; future batch will trigger
///             PostBillCommandHandler to write JE + AP open item
///   Voided  → frozen; future batch will reverse the posting
///
/// V1 only supports Category-mode lines (line points to an
/// expense / asset account). Item-mode lines (line points to an
/// inventory item) land with the Inventory batch — the schema's
/// <c>expense_account_id</c> column already accommodates the
/// Category mode and the existing
/// <c>EnsureInventoryGradeBillLineColumnsAsync</c> helper layers
/// item-grade columns when Inventory ships.
/// </summary>
public interface IBillStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BillSummary>> ListAsync(
        CompanyId companyId,
        BillListFilter filter,
        CancellationToken cancellationToken);

    Task<BillRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken);

    Task<BillRecord> CreateAsync(
        CompanyId companyId,
        UserId createdByUserId,
        BillUpsertInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates a Draft bill. Throws <see cref="InvalidOperationException"/>
    /// when the bill is no longer Draft.
    /// </summary>
    Task<BillRecord?> UpdateAsync(
        CompanyId companyId,
        Guid billId,
        BillUpsertInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transitions Draft → Posted. V1 only stamps status / posted_at;
    /// the heavy GL write lands when the PO + Inventory batch wires
    /// in <c>PostBillCommandHandler</c>.
    /// </summary>
    Task<BillRecord?> PostAsync(
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transitions Posted → Voided. V1 only stamps status; reversing
    /// the GL entries lands with the posting wiring batch.
    /// </summary>
    Task<BillRecord?> VoidAsync(
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken);
}

public sealed record BillListFilter(
    bool IncludeDrafts,
    string? Status,
    Guid? VendorId,
    DateOnly? FromDate,
    DateOnly? ToDate);

public sealed record BillSummary(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string BillNumber,
    Guid VendorId,
    string VendorName,
    DateOnly BillDate,
    DateOnly DueDate,
    string Status,
    string DocumentCurrencyCode,
    decimal TotalAmount,
    string? SourcePurchaseOrderNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BillRecord(
    Guid Id,
    CompanyId CompanyId,
    string EntityNumber,
    string BillNumber,
    string Status,
    Guid VendorId,
    string VendorName,
    DateOnly BillDate,
    DateOnly DueDate,
    string DocumentCurrencyCode,
    string BaseCurrencyCode,
    decimal FxRate,
    string FxSource,
    decimal SubtotalAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? Memo,
    Guid? PaymentTermId,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    DateTimeOffset? PostedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BillLineRecord> Lines);

public sealed record BillLineRecord(
    Guid Id,
    Guid BillId,
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount);

public sealed record BillUpsertInput(
    string BillNumber,
    Guid VendorId,
    DateOnly BillDate,
    DateOnly DueDate,
    string DocumentCurrencyCode,
    decimal? FxRate,
    string? Memo,
    Guid? PaymentTermId,
    Guid? SourcePurchaseOrderId,
    string? SourcePurchaseOrderNumber,
    IReadOnlyList<BillLineInput> Lines,
    // Optimistic-concurrency token threaded down from the route layer.
    // When non-null, UpdateAsync's UPDATE narrows on updated_at; a
    // mismatch raises ConcurrencyConflictException so the route can
    // surface 409. Null = legacy opt-out behaviour preserved.
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record BillLineInput(
    int LineNumber,
    Guid ExpenseAccountId,
    string Description,
    decimal LineAmount,
    Guid? TaxCodeId,
    decimal TaxAmount);

public static class BillStatus
{
    public const string Draft = "draft";
    public const string Posted = "posted";
    public const string PartiallyPaid = "partially_paid";
    public const string Paid = "paid";
    public const string Voided = "voided";
    public const string Reversed = "reversed";

    public static bool IsValid(string? status) =>
        status is Draft or Posted or PartiallyPaid or Paid or Voided or Reversed;

    public static bool IsEditable(string status) =>
        status is Draft;

    public static bool IsTerminal(string status) =>
        status is Voided or Reversed;
}
