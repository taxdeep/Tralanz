using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// P0-3b-1 (AUDIT_2026-05-20 C3): appends the Dr/Cr journal entry that
/// keeps the GL Inventory Asset balance reconciled to the inventory
/// subledger after an Inventory Adjustment (Gain / Loss) or Write-off
/// posts.
///
/// The poster runs INSIDE the inventory store's existing transaction:
/// caller passes the open <see cref="NpgsqlConnection"/> +
/// <see cref="NpgsqlTransaction"/>, the poster writes the JE on the same
/// tx, and the store decides whether to commit or roll back. If the
/// poster throws, the inventory store's catch block rolls back the
/// whole tx, so subledger writes do not persist without the matching
/// GL entry (closes the C3 sub-ledger / GL drift risk).
///
/// Idempotency is enforced by the JE table's existing
/// <c>(company_id, source_type, source_id)</c> probe: re-running the
/// same posting returns the previously-posted JE's identifiers without
/// inserting a duplicate row. The probe + insert run in the same tx,
/// so a concurrent retry that lands a new row is naturally serialised
/// by the inventory_documents row's outer transaction.
///
/// JE shapes (per project_business_rules_2026_05_20.md Q2=A):
///   Gain      → Dr Inventory Asset / Cr Inventory Adjustment
///   Loss      → Dr Inventory Adjustment / Cr Inventory Asset
///   WriteOff  → Dr Inventory Adjustment / Cr Inventory Asset
///
/// Both accounts are resolved by company-level SystemRole bindings
/// (<c>inventory_asset</c> and <c>inventory_adjustment</c>) which the
/// platform's starter chart of accounts pins to 14000 Inventory and
/// 64600 Inventory Adjustment respectively. Per-item account overrides
/// are NOT honoured in this iteration — Q2=A specifies a single
/// adjustment account per company.
/// </summary>
public interface IInventoryAdjustmentGlPoster
{
    /// <summary>
    /// Probes for an existing JE keyed by
    /// <c>(source_type='inventory_adjustment_gl', source_id=inventoryDocumentId)</c>;
    /// if found, returns the existing identifiers with
    /// <see cref="InventoryAdjustmentGlPostingResult.AlreadyPosted"/> = true.
    /// Otherwise resolves accounts, reserves entity + display numbers,
    /// and inserts the JE header, two JE lines, and two ledger entries
    /// on the passed connection/transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>inventory_asset</c> or <c>inventory_adjustment</c>
    /// is not bound to an active account, when the posting period is
    /// closed, or when the request's <see cref="InventoryAdjustmentGlPostingRequest.TotalCostBase"/>
    /// is not strictly positive.
    /// </exception>
    Task<InventoryAdjustmentGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryAdjustmentGlPostingRequest request,
        CancellationToken cancellationToken);
}

public enum InventoryAdjustmentGlKind
{
    Gain,
    Loss,
    WriteOff
}

public sealed record InventoryAdjustmentGlPostingRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid InventoryDocumentId,
    string InventoryDocumentNumber,
    DateOnly PostingDate,
    string BaseCurrencyCode,
    InventoryAdjustmentGlKind Kind,
    decimal TotalCostBase);

public sealed record InventoryAdjustmentGlPostingResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyPosted);
