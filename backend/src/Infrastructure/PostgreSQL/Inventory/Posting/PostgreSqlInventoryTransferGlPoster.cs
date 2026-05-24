using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// PostgreSQL implementation of <see cref="IInventoryTransferGlPoster"/>.
/// Each transfer leg (Ship / Receive) produces a single 2-line JE
/// keyed by <c>(company_id, source_type, source_id=transfer_id)</c>,
/// with the source_type encoding which leg so the two legs are
/// independently idempotent.
///
/// Refactor 2026-05-23: SQL plumbing moved into
/// <see cref="PostgreSqlInventoryGlHelpers"/>. Behavior unchanged —
/// all six transfer tests pass before and after.
/// </summary>
public sealed class PostgreSqlInventoryTransferGlPoster : IInventoryTransferGlPoster
{
    private const string ShipSourceType = "inventory_transfer_ship_gl";
    private const string ReceiveSourceType = "inventory_transfer_receive_gl";
    private const string PostingRole = "inventory_transfer";
    private const string ExchangeRateSource = "base_currency_inventory_transfer";
    private const string InventoryAssetSystemRole = "inventory_asset";
    private const string InventoryAssetSystemKey = "inventory:asset";

    public async Task<InventoryTransferGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryTransferGlPostingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);

        if (request.TotalCostBase <= 0m)
        {
            throw new InvalidOperationException(
                $"Inventory transfer GL posting requires a strictly positive total cost; got {request.TotalCostBase}.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Base currency code is required for inventory transfer GL posting.");
        }

        var sourceType = request.Leg switch
        {
            InventoryTransferGlLeg.Ship => ShipSourceType,
            InventoryTransferGlLeg.Receive => ReceiveSourceType,
            _ => throw new InvalidOperationException($"Unsupported inventory transfer GL leg '{request.Leg}'.")
        };

        // 1. Idempotency probe per-leg (so Ship + Receive are independent).
        var existing = await PostgreSqlInventoryGlHelpers.TryFindExistingByIdempotencyAsync(
            connection, transaction, request.CompanyId, sourceType, request.TransferId, cancellationToken);
        if (existing is not null)
        {
            return new InventoryTransferGlPostingResult(
                existing.Value.Id,
                existing.Value.DisplayNumber,
                AlreadyPosted: true);
        }

        // 2. Resolve the inventory_asset account; both Dr and Cr legs
        //    point at it (V1 single-account model). When the COA
        //    splits, the credit-side resolution changes per leg.
        var inventoryAssetAccountId = await PostgreSqlInventoryGlHelpers.ResolveAccountIdAsync(
            connection, transaction, request.CompanyId, cancellationToken,
            InventoryAssetSystemRole, InventoryAssetSystemKey);
        if (inventoryAssetAccountId is null)
        {
            throw new InvalidOperationException(
                "No active account is bound to system role 'inventory_asset' (starter COA: 14000 Inventory). " +
                "Pin the Inventory account's system role via the activation wizard before posting transfer legs.");
        }

        // 3. Period gate.
        await PostgreSqlInventoryGlHelpers.EnsurePostingPeriodOpenAsync(
            connection, transaction, request.CompanyId, request.PostingDate, cancellationToken);

        // 4. Reserve entity + display numbers.
        var entityNumber = await PostgreSqlInventoryGlHelpers.ReserveEntityNumberAsync(
            connection, transaction, request.CompanyId, request.PostingDate.Year, cancellationToken);
        var displayNumber = await PostgreSqlInventoryGlHelpers.ReserveDisplayNumberAsync(
            connection, transaction, request.CompanyId, cancellationToken);

        // 5. Build per-leg JE descriptions.
        var amount = PostgreSqlInventoryGlHelpers.Round6(request.TotalCostBase);
        var (debitDescription, creditDescription) = request.Leg switch
        {
            InventoryTransferGlLeg.Ship => (
                $"Transfer in-transit hold — {request.TransferNumber}",
                $"Transfer ship — {request.LegDocumentNumber}"),
            InventoryTransferGlLeg.Receive => (
                $"Transfer receive — {request.LegDocumentNumber}",
                $"Transfer in-transit release — {request.TransferNumber}"),
            _ => throw new InvalidOperationException($"Unsupported inventory transfer GL leg '{request.Leg}'.")
        };

        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var legKey = request.Leg.ToString().ToLowerInvariant();
        var idempotencyKey = $"inventory-transfer-{legKey}-gl:{request.CompanyId.Value}:{request.TransferId}";

        await PostgreSqlInventoryGlHelpers.InsertJournalEntryAsync(
            connection, transaction,
            journalEntryId, request.CompanyId, entityNumber, displayNumber,
            sourceType, request.TransferId,
            request.BaseCurrencyCode, request.PostingDate, ExchangeRateSource,
            amount, idempotencyKey, postedAt, request.UserId,
            cancellationToken);

        var debitLineId = Guid.NewGuid();
        await PostgreSqlInventoryGlHelpers.InsertJournalEntryLineAsync(
            connection, transaction,
            debitLineId, journalEntryId, request.CompanyId,
            lineNumber: 1, accountId: inventoryAssetAccountId.Value, description: debitDescription,
            debit: amount, credit: 0m, PostingRole,
            cancellationToken);

        var creditLineId = Guid.NewGuid();
        await PostgreSqlInventoryGlHelpers.InsertJournalEntryLineAsync(
            connection, transaction,
            creditLineId, journalEntryId, request.CompanyId,
            lineNumber: 2, accountId: inventoryAssetAccountId.Value, description: creditDescription,
            debit: 0m, credit: amount, PostingRole,
            cancellationToken);

        await PostgreSqlInventoryGlHelpers.InsertLedgerEntryAsync(
            connection, transaction,
            ledgerId: Guid.NewGuid(), journalEntryId, debitLineId,
            request.CompanyId, request.PostingDate, inventoryAssetAccountId.Value,
            debit: amount, credit: 0m, request.BaseCurrencyCode,
            cancellationToken);

        await PostgreSqlInventoryGlHelpers.InsertLedgerEntryAsync(
            connection, transaction,
            ledgerId: Guid.NewGuid(), journalEntryId, creditLineId,
            request.CompanyId, request.PostingDate, inventoryAssetAccountId.Value,
            debit: 0m, credit: amount, request.BaseCurrencyCode,
            cancellationToken);

        return new InventoryTransferGlPostingResult(
            journalEntryId,
            displayNumber,
            AlreadyPosted: false);
    }
}
