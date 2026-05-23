using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// P0-3b-2 (AUDIT_2026-05-20 C3 closure): appends the audit-trail GL
/// journal entry that documents a Manufacturing run in the general
/// ledger so the GL has a record of cost movement even though net
/// balance is unchanged.
///
/// Accounting model (V1, single Inventory-Asset account COA):
///   Dr 14000 Inventory (= finished-goods receipt amount)
///   Cr 14000 Inventory (= raw-materials issue amount)
///
/// Both legs point at the SAME 14000 account because the starter COA
/// does not split Raw Materials / WIP / Finished Goods. In V1 with no
/// manufacturing variance, the consumed raw-material cost equals the
/// produced finished-good cost, so total debit == total credit ==
/// totalConsumedCostBase. The JE balance impact is zero, but the JE
/// row provides:
///   * an audit-trail record in journal_entries / journal_entry_lines
///     tied to the manufacturing run via source_id
///   * a uniform "every inventory mutation emits a JE" invariant
///   * a reverse-compensation handle for future Manufacturing-reverse
///     flows (same shape, swapped Dr/Cr)
///
/// When the COA evolves to multi-account inventory (RM / WIP / FG),
/// the credit side becomes Cr WIP (or Cr Raw Materials) and a sibling
/// JE on Receipt becomes Dr Finished Goods / Cr WIP — at which point
/// the V1 net-zero JE turns into real cost movement. That migration
/// is tracked separately; nothing in this PR blocks it.
///
/// Atomicity: same-tx as the inventory subledger writes; caller
/// passes its open connection/transaction. Failure cascades to the
/// inventory store's outer catch and rolls back every subledger write.
///
/// Idempotency: the
/// (company_id, source_type='inventory_manufacturing_gl', source_id=runId)
/// probe runs on the caller's tx; a re-run returns the prior JE's
/// identifiers without inserting a duplicate row.
/// </summary>
public interface IInventoryManufacturingGlPoster
{
    Task<InventoryManufacturingGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryManufacturingGlPostingRequest request,
        CancellationToken cancellationToken);
}

public sealed record InventoryManufacturingGlPostingRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid ManufacturingRunId,
    string ManufacturingRunNumber,
    string IssueDocumentNumber,
    string ReceiptDocumentNumber,
    DateOnly PostingDate,
    string BaseCurrencyCode,
    decimal TotalConsumedCostBase);

public sealed record InventoryManufacturingGlPostingResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyPosted);
