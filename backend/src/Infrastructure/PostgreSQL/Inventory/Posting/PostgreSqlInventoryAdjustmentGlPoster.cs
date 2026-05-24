using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// PostgreSQL implementation of <see cref="IInventoryAdjustmentGlPoster"/>.
/// Writes the JE header + 2 lines + 2 ledger entries on the inventory
/// store's existing transaction so the subledger and GL commit (or
/// roll back) together.
///
/// Idempotency: <see cref="PostgreSqlInventoryGlHelpers.TryFindExistingByIdempotencyAsync"/>
/// probes <c>journal_entries</c> on
/// <c>(company_id, source_type='inventory_adjustment_gl', source_id=docId)</c>
/// before any INSERT. A retry against the same source returns the
/// prior JE's identifiers with <c>AlreadyPosted=true</c>.
///
/// Per-Q2=A Dr/Cr mapping:
///   Gain     → Dr 14000 Inventory Asset / Cr 64600 Inventory Adjustment
///   Loss     → Dr 64600 Inventory Adjustment / Cr 14000 Inventory Asset
///   WriteOff → same as Loss (single adjustment-account model)
///
/// Refactor 2026-05-23: SQL plumbing (idempotency probe, account
/// resolve, period gate, numbering, INSERTs) moved into
/// <see cref="PostgreSqlInventoryGlHelpers"/> so the three sibling
/// posters (Adjustment / Manufacturing / Transfer) share the same
/// code path. Behavior unchanged — all six adjustment tests pass
/// before and after the refactor.
/// </summary>
public sealed class PostgreSqlInventoryAdjustmentGlPoster : IInventoryAdjustmentGlPoster
{
    private const string SourceType = "inventory_adjustment_gl";
    private const string PostingRole = "inventory_adjustment";
    private const string ExchangeRateSource = "base_currency_inventory_adjustment";
    private const string InventoryAssetSystemRole = "inventory_asset";
    private const string InventoryAssetSystemKey = "inventory:asset";
    private const string InventoryAdjustmentSystemRole = "inventory_adjustment";
    private const string InventoryAdjustmentSystemKey = "inventory:adjustment";

    public async Task<InventoryAdjustmentGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryAdjustmentGlPostingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);

        if (request.TotalCostBase <= 0m)
        {
            throw new InvalidOperationException(
                $"Inventory adjustment GL posting requires a strictly positive total cost; got {request.TotalCostBase}.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Base currency code is required for inventory adjustment GL posting.");
        }

        // 1. Idempotency probe — re-runs short-circuit on existing JE.
        var existing = await PostgreSqlInventoryGlHelpers.TryFindExistingByIdempotencyAsync(
            connection, transaction, request.CompanyId, SourceType, request.InventoryDocumentId, cancellationToken);
        if (existing is not null)
        {
            return new InventoryAdjustmentGlPostingResult(
                existing.Value.Id,
                existing.Value.DisplayNumber,
                AlreadyPosted: true);
        }

        // 2. Resolve the two SystemRole-pinned accounts.
        var inventoryAssetAccountId = await PostgreSqlInventoryGlHelpers.ResolveAccountIdAsync(
            connection, transaction, request.CompanyId, cancellationToken,
            InventoryAssetSystemRole, InventoryAssetSystemKey);
        if (inventoryAssetAccountId is null)
        {
            throw new InvalidOperationException(
                "No active account is bound to system role 'inventory_asset' (starter COA: 14000 Inventory). " +
                "Pin the Inventory account's system role via the activation wizard before posting inventory adjustments.");
        }

        var inventoryAdjustmentAccountId = await PostgreSqlInventoryGlHelpers.ResolveAccountIdAsync(
            connection, transaction, request.CompanyId, cancellationToken,
            InventoryAdjustmentSystemRole, InventoryAdjustmentSystemKey);
        if (inventoryAdjustmentAccountId is null)
        {
            throw new InvalidOperationException(
                "No active account is bound to system role 'inventory_adjustment' (starter COA: 64600 Inventory Adjustment). " +
                "Pin the Inventory Adjustment account's system role via the activation wizard before posting inventory adjustments.");
        }

        // 3. Posting-period gate.
        await PostgreSqlInventoryGlHelpers.EnsurePostingPeriodOpenAsync(
            connection, transaction, request.CompanyId, request.PostingDate, cancellationToken);

        // 4. Reserve entity_number + display_number on the inventory tx.
        var entityNumber = await PostgreSqlInventoryGlHelpers.ReserveEntityNumberAsync(
            connection, transaction, request.CompanyId, request.PostingDate.Year, cancellationToken);
        var displayNumber = await PostgreSqlInventoryGlHelpers.ReserveDisplayNumberAsync(
            connection, transaction, request.CompanyId, cancellationToken);

        // 5. Compute Dr/Cr direction from the adjustment kind.
        var amount = PostgreSqlInventoryGlHelpers.Round6(request.TotalCostBase);
        var (debitAccountId, creditAccountId) = request.Kind switch
        {
            InventoryAdjustmentGlKind.Gain => (inventoryAssetAccountId.Value, inventoryAdjustmentAccountId.Value),
            InventoryAdjustmentGlKind.Loss => (inventoryAdjustmentAccountId.Value, inventoryAssetAccountId.Value),
            InventoryAdjustmentGlKind.WriteOff => (inventoryAdjustmentAccountId.Value, inventoryAssetAccountId.Value),
            _ => throw new InvalidOperationException($"Unsupported inventory adjustment GL kind '{request.Kind}'.")
        };

        // 6. Insert JE header + 2 lines + 2 ledger entries.
        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var idempotencyKey = $"inventory-adjustment-gl:{request.CompanyId.Value}:{request.InventoryDocumentId}";
        var description = BuildLineDescription(request);

        await PostgreSqlInventoryGlHelpers.InsertJournalEntryAsync(
            connection, transaction,
            journalEntryId, request.CompanyId, entityNumber, displayNumber,
            SourceType, request.InventoryDocumentId,
            request.BaseCurrencyCode, request.PostingDate, ExchangeRateSource,
            amount, idempotencyKey, postedAt, request.UserId,
            cancellationToken);

        var debitLineId = Guid.NewGuid();
        await PostgreSqlInventoryGlHelpers.InsertJournalEntryLineAsync(
            connection, transaction,
            debitLineId, journalEntryId, request.CompanyId,
            lineNumber: 1, accountId: debitAccountId, description: description,
            debit: amount, credit: 0m, PostingRole,
            cancellationToken);

        var creditLineId = Guid.NewGuid();
        await PostgreSqlInventoryGlHelpers.InsertJournalEntryLineAsync(
            connection, transaction,
            creditLineId, journalEntryId, request.CompanyId,
            lineNumber: 2, accountId: creditAccountId, description: description,
            debit: 0m, credit: amount, PostingRole,
            cancellationToken);

        await PostgreSqlInventoryGlHelpers.InsertLedgerEntryAsync(
            connection, transaction,
            ledgerId: Guid.NewGuid(), journalEntryId, debitLineId,
            request.CompanyId, request.PostingDate, debitAccountId,
            debit: amount, credit: 0m, request.BaseCurrencyCode,
            cancellationToken);

        await PostgreSqlInventoryGlHelpers.InsertLedgerEntryAsync(
            connection, transaction,
            ledgerId: Guid.NewGuid(), journalEntryId, creditLineId,
            request.CompanyId, request.PostingDate, creditAccountId,
            debit: 0m, credit: amount, request.BaseCurrencyCode,
            cancellationToken);

        return new InventoryAdjustmentGlPostingResult(
            journalEntryId,
            displayNumber,
            AlreadyPosted: false);
    }

    private static string BuildLineDescription(InventoryAdjustmentGlPostingRequest request)
    {
        var label = request.Kind switch
        {
            InventoryAdjustmentGlKind.Gain => "Inventory adjustment gain",
            InventoryAdjustmentGlKind.Loss => "Inventory adjustment loss",
            InventoryAdjustmentGlKind.WriteOff => "Inventory write-off",
            _ => "Inventory adjustment"
        };
        return $"{label} — {request.InventoryDocumentNumber}";
    }
}
