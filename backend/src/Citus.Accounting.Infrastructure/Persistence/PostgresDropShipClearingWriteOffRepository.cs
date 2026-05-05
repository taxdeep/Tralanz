using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresDropShipClearingWriteOffRepository : IDropShipClearingWriteOffRepository
{
    private const decimal AmountTolerance = 0.005m;

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresDropShipClearingWriteOffRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<DropShipClearingWriteOffDocument> PrepareAsync(
        CompanyId companyId,
        UserId userId,
        Guid itemId,
        decimal expectedNetClearingBase,
        string? memo,
        CancellationToken cancellationToken)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("Item id is required.", nameof(itemId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // Verify the item exists, is drop-ship, and capture display + per-
        // item clearing override.
        var item = await ReadItemAsync(scope, companyId.Value, itemId, cancellationToken);
        if (item is null)
        {
            throw new InvalidOperationException(
                $"Item {itemId:D} was not found for company {companyId.Value:D}.");
        }
        if (!string.Equals(item.Value.ItemKind, "drop_ship", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Write-off is only valid for drop-ship items. Item {item.Value.ItemCode} has kind '{item.Value.ItemKind}'.");
        }

        // Re-read live residual and compare against operator's expectation.
        var liveNet = await ReadLiveNetClearingAsync(scope, companyId.Value, itemId, cancellationToken);
        if (liveNet == 0m)
        {
            throw new InvalidOperationException(
                $"Drop-ship Clearing for item {item.Value.ItemCode} is already balanced — nothing to write off.");
        }
        if (Math.Abs(liveNet - expectedNetClearingBase) > AmountTolerance)
        {
            throw new InvalidOperationException(
                $"Drop-ship Clearing residual for item {item.Value.ItemCode} has changed since the workbench was loaded " +
                $"(expected {expectedNetClearingBase:N2}, currently {liveNet:N2}). Refresh the workbench and try again.");
        }

        // Resolve clearing + variance accounts. Clearing: per-item
        // override → company-level system_role='drop_ship_clearing'.
        // Variance: company-level system_role='purchase_price_variance'
        // (no per-item override — drop-ship variance is rare enough that
        // a per-item account would be over-engineering for V1).
        var clearingAccountId = item.Value.DropShipClearingAccountId;
        if (clearingAccountId is null || clearingAccountId.Value == Guid.Empty)
        {
            clearingAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
                scope, companyId.Value, cancellationToken, "drop_ship_clearing");
        }
        if (clearingAccountId is null || clearingAccountId.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Cannot resolve the Drop-ship Clearing account. Set a per-item account on the item master, " +
                "or seed CoA 21600 with system_role='drop_ship_clearing' via the activation wizard.");
        }

        var varianceAccountId = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope, companyId.Value, cancellationToken, "purchase_price_variance");
        if (varianceAccountId is null || varianceAccountId.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Cannot resolve the Purchase Price Variance account. Seed CoA 51100 with system_role='purchase_price_variance' " +
                "via the activation wizard before writing off drop-ship clearing residuals.");
        }

        var baseCurrency = await ReadBaseCurrencyAsync(scope, companyId.Value, cancellationToken);
        var documentDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var docId = Guid.NewGuid();
        var idShort = docId.ToString("N")[..12].ToUpperInvariant();
        var entityNumber = new EntityNumber($"EN-DSWO-{idShort}");
        var displayNumber = new DocumentNumber($"DSWO-{idShort}");

        return new DropShipClearingWriteOffDocument(
            id: docId,
            companyId: companyId,
            entityNumber: entityNumber,
            displayNumber: displayNumber,
            documentDate: documentDate,
            itemId: itemId,
            itemCode: item.Value.ItemCode,
            dropShipClearingAccountId: clearingAccountId.Value,
            varianceAccountId: varianceAccountId.Value,
            netClearingAmountBase: liveNet,
            baseCurrencyCode: new CurrencyCode(baseCurrency),
            memo: memo);
    }

    private static async Task<ItemRow?> ReadItemAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select item_code, item_kind, default_drop_ship_clearing_account_id
            from inventory_items
            where company_id = @company_id and id = @item_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ItemRow(
            ItemCode: reader.GetString(0),
            ItemKind: reader.GetString(1),
            DropShipClearingAccountId: reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task<decimal> ReadLiveNetClearingAsync(
        PostgresCommandScope scope,
        Guid companyId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            with bill_side as (
              select coalesce(sum(bl.line_amount * b.fx_rate), 0) as billed_base
              from bill_lines bl
              join bills b on b.id = bl.bill_id
              where b.company_id = @company_id
                and b.status = 'posted'
                and bl.item_id = @item_id
            ),
            invoice_side as (
              select coalesce(sum(il.quantity * coalesce(i.default_purchase_price, 0)), 0) as invoiced_base
              from invoice_lines il
              join invoices inv on inv.id = il.invoice_id
              join inventory_items i on i.id = il.item_id
              where inv.company_id = @company_id
                and inv.status = 'posted'
                and il.item_id = @item_id
            )
            select
              (select billed_base from bill_side) - (select invoiced_base from invoice_side) as net_clearing_base;
            """);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("item_id", itemId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull) return 0m;
        return Math.Round((decimal)result, 6, MidpointRounding.ToEven);
    }

    private static async Task<string> ReadBaseCurrencyAsync(
        PostgresCommandScope scope,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            "select base_currency_code from companies where id = @company_id limit 1;");
        command.Parameters.AddWithValue("company_id", companyId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string s ? s.Trim().ToUpperInvariant() : "USD";
    }

    private readonly record struct ItemRow(
        string ItemCode,
        string ItemKind,
        Guid? DropShipClearingAccountId);
}
