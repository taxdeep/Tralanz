using Npgsql;

namespace Infrastructure.PostgreSQL.Inventory.Posting;

/// <summary>
/// PostgreSQL implementation of <see cref="IInventoryManufacturingGlPoster"/>.
/// Mirrors the adjustment poster's same-tx pattern via the shared
/// <see cref="PostgreSqlInventoryGlHelpers"/>. Emits a single
/// audit-trail JE with both Dr/Cr legs pointing at the company's
/// <c>inventory_asset</c> account (V1 single-account COA model — see
/// <see cref="IInventoryManufacturingGlPoster"/> doc for the
/// rationale and the multi-account COA upgrade path).
///
/// Refactor 2026-05-23: SQL plumbing moved into
/// <see cref="PostgreSqlInventoryGlHelpers"/>. Behavior unchanged.
/// </summary>
public sealed class PostgreSqlInventoryManufacturingGlPoster : IInventoryManufacturingGlPoster
{
    private const string SourceType = "inventory_manufacturing_gl";
    private const string PostingRole = "inventory_manufacturing";
    private const string ExchangeRateSource = "base_currency_inventory_manufacturing";
    private const string InventoryAssetSystemRole = "inventory_asset";
    private const string InventoryAssetSystemKey = "inventory:asset";

    public async Task<InventoryManufacturingGlPostingResult> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InventoryManufacturingGlPostingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);

        if (request.TotalConsumedCostBase <= 0m)
        {
            throw new InvalidOperationException(
                $"Inventory manufacturing GL posting requires a strictly positive consumed cost; got {request.TotalConsumedCostBase}.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Base currency code is required for inventory manufacturing GL posting.");
        }

        // 1. Idempotency probe on (company_id, source_type, manufacturing_run_id).
        var existing = await PostgreSqlInventoryGlHelpers.TryFindExistingByIdempotencyAsync(
            connection, transaction, request.CompanyId, SourceType, request.ManufacturingRunId, cancellationToken);
        if (existing is not null)
        {
            return new InventoryManufacturingGlPostingResult(
                existing.Value.Id,
                existing.Value.DisplayNumber,
                AlreadyPosted: true);
        }

        // 2. Resolve the single inventory_asset account; both Dr and
        //    Cr legs point at it (V1 single-account model).
        var inventoryAssetAccountId = await PostgreSqlInventoryGlHelpers.ResolveAccountIdAsync(
            connection, transaction, request.CompanyId, cancellationToken,
            InventoryAssetSystemRole, InventoryAssetSystemKey);
        if (inventoryAssetAccountId is null)
        {
            throw new InvalidOperationException(
                "No active account is bound to system role 'inventory_asset' (starter COA: 14000 Inventory). " +
                "Pin the Inventory account's system role via the activation wizard before posting manufacturing runs.");
        }

        // 3. Period gate.
        await PostgreSqlInventoryGlHelpers.EnsurePostingPeriodOpenAsync(
            connection, transaction, request.CompanyId, request.PostingDate, cancellationToken);

        // 4. Reserve entity_number + display_number.
        var entityNumber = await PostgreSqlInventoryGlHelpers.ReserveEntityNumberAsync(
            connection, transaction, request.CompanyId, request.PostingDate.Year, cancellationToken);
        var displayNumber = await PostgreSqlInventoryGlHelpers.ReserveDisplayNumberAsync(
            connection, transaction, request.CompanyId, cancellationToken);

        // 5. Build the audit-trail JE: Dr 14000 (receipt side) / Cr 14000 (issue side).
        var amount = PostgreSqlInventoryGlHelpers.Round6(request.TotalConsumedCostBase);
        var debitDescription = $"Manufacturing receipt — {request.ReceiptDocumentNumber} (run {request.ManufacturingRunNumber})";
        var creditDescription = $"Manufacturing issue — {request.IssueDocumentNumber} (run {request.ManufacturingRunNumber})";

        var journalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var idempotencyKey = $"inventory-manufacturing-gl:{request.CompanyId.Value}:{request.ManufacturingRunId}";

        await PostgreSqlInventoryGlHelpers.InsertJournalEntryAsync(
            connection, transaction,
            journalEntryId, request.CompanyId, entityNumber, displayNumber,
            SourceType, request.ManufacturingRunId,
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

        return new InventoryManufacturingGlPostingResult(
            journalEntryId,
            displayNumber,
            AlreadyPosted: false);
    }
}
