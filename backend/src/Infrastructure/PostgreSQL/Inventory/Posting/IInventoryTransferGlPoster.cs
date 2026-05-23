using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// P0-3b-3 (AUDIT_2026-05-20 C3 final closure): appends the audit-trail
/// GL journal entry that documents a warehouse-transfer leg (Ship OR
/// Receive) in the general ledger. Same-tx + idempotency mechanics as
/// the Adjustment (P0-3b-1) and Manufacturing (P0-3b-2) posters.
///
/// Transfers are a two-step flow (Ship and Receive can happen on
/// different days; in between, the inventory is "in transit"). Each
/// leg gets its own JE keyed by source_type so they're independently
/// idempotent:
///
///   Ship leg     → source_type='inventory_transfer_ship_gl'
///   Receive leg  → source_type='inventory_transfer_receive_gl'
///
/// Both legs use source_id = warehouse_transfer.id so a single
/// transfer record threads through to both JEs.
///
/// Accounting model (V1, single Inventory-Asset account COA):
///   Ship leg JE:
///     Dr 14000 Inventory   (in-transit hold side, totalShippedCostBase)
///     Cr 14000 Inventory   (source-warehouse release side)
///   Receive leg JE:
///     Dr 14000 Inventory   (destination-warehouse take side)
///     Cr 14000 Inventory   (in-transit release side)
///
/// Both legs are net-zero in V1 (same account on both sides). The JE
/// rows provide the GL audit trail; when the COA evolves to per-
/// warehouse or in-transit accounts, the credit-side resolution
/// changes per leg without touching the rest of this flow.
///
/// In V1 no manufacturing variance, ship's totalCost ==
/// receive's totalCost (sum of cost layers consumed at source ==
/// sum of cost layers created at destination). Each leg's JE total
/// matches its own leg's subledger writes, not the other leg's.
/// </summary>
public interface IInventoryTransferGlPoster
{
    Task<InventoryTransferGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryTransferGlPostingRequest request,
        CancellationToken cancellationToken);
}

public enum InventoryTransferGlLeg
{
    Ship,
    Receive
}

public sealed record InventoryTransferGlPostingRequest(
    CompanyId CompanyId,
    UserId UserId,
    Guid TransferId,
    string TransferNumber,
    string LegDocumentNumber,
    DateOnly PostingDate,
    string BaseCurrencyCode,
    InventoryTransferGlLeg Leg,
    decimal TotalCostBase);

public sealed record InventoryTransferGlPostingResult(
    Guid JournalEntryId,
    string JournalEntryDisplayNumber,
    bool AlreadyPosted);
