using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M6 iter 3 implementation. Reads the invoice header for currency +
/// posting date, joins invoice_lines to inventory_items where item_kind
/// = 'drop_ship', aggregates qty × default_purchase_price per item, and
/// builds an <see cref="InvoiceDropShipCogsPostingDocument"/>.
///
/// Cost-basis trade-off: V1 uses the item-master purchase price as a
/// stable proxy for the actual vendor cost. Per-bill matching would be
/// more accurate but requires bill timing alignment (the invoice may
/// post before the bill arrives). The Drop-ship Clearing aging
/// workbench (iter 4) surfaces residuals when invoice-side and bill-side
/// amounts diverge.
/// </summary>
public sealed class PostgresInvoiceDropShipCogsPostingRepository : IInvoiceDropShipCogsPostingRepository
{
    private const string DropShipCogsSourceType = "invoice_drop_ship_cogs";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresInvoiceDropShipCogsPostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<InvoiceDropShipCogsPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        if (invoiceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Invoice document id is required.", nameof(invoiceDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // Idempotency probe — JE already exists for this invoice's drop-ship hook.
        var existing = await TryReadExistingJournalAsync(scope, companyId, invoiceDocumentId, cancellationToken);
        if (existing is not null)
        {
            return new InvoiceDropShipCogsPostingPreparation(
                Document: null,
                ExistingJournalEntryId: existing.Value.Id,
                ExistingJournalEntryDisplayNumber: existing.Value.DisplayNumber);
        }

        // Header — invoice must exist + be posted; capture base currency
        // and the date the JE should book under (invoice date matches the
        // M3 sister that uses inventory posting_date).
        var header = await ReadHeaderAsync(scope, companyId, invoiceDocumentId, cancellationToken);
        if (header is null)
        {
            throw new InvalidOperationException(
                $"Invoice {invoiceDocumentId:D} was not found for company {companyId.Value:D}.");
        }
        if (!string.Equals(header.Value.Status, "posted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Invoice {invoiceDocumentId:D} must be posted before drop-ship COGS can be journalised. Current status: {header.Value.Status}.");
        }

        // Body — sum (qty × default_purchase_price) per drop-ship item.
        // Non-drop-ship lines are filtered out by the WHERE clause; the
        // join also surfaces the per-item COGS / clearing account
        // overrides (typically null for drop-ship since the items page
        // hides COGS picker, but kept for symmetry with M3).
        var lines = await ReadLineCandidatesAsync(scope, companyId, invoiceDocumentId, cancellationToken);
        if (lines.Count == 0)
        {
            // No drop-ship lines — nothing to journalise. Caller treats
            // this as a no-op (returns null document + null existing-id).
            return new InvoiceDropShipCogsPostingPreparation(
                Document: null,
                ExistingJournalEntryId: null,
                ExistingJournalEntryDisplayNumber: null);
        }

        // Resolve company-level fallbacks once. Drop-ship items typically
        // pin neither account on themselves (the items page hides COGS
        // picker entirely; the clearing picker is optional), so the
        // company-level system_role lookup carries the load.
        var fallbackCogs = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope, companyId, cancellationToken, "cost_of_goods_sold", "inventory:cogs");
        var fallbackClearing = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope, companyId, cancellationToken, "drop_ship_clearing");

        var documentLines = new List<InvoiceDropShipCogsPostingDocumentLine>();
        var lineNumber = 0;
        foreach (var candidate in lines)
        {
            if (candidate.PurchasePrice is null || candidate.PurchasePrice.Value <= 0m)
            {
                throw new InvalidOperationException(
                    $"Drop-ship item {candidate.ItemCode} ({candidate.ItemId:D}) has no purchase price set on the item master. " +
                    "Set Default purchase price on the Items page before posting an invoice that references it — " +
                    "the COGS leg uses purchase price × qty as its cost basis.");
            }

            var amountBase = Math.Round(
                candidate.QuantitySum * candidate.PurchasePrice.Value,
                6,
                MidpointRounding.ToEven);
            if (amountBase <= 0m)
            {
                continue;
            }

            var cogsAccountId = candidate.ItemCogsAccountId ?? fallbackCogs;
            if (cogsAccountId is null || cogsAccountId.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Drop-ship item {candidate.ItemCode} has no COGS account configured and the company has no account " +
                    "bound to system role 'cost_of_goods_sold'. Seed CoA 51000 with system_role='cost_of_goods_sold' " +
                    "via the activation wizard.");
            }

            var clearingAccountId = candidate.ItemDropShipClearingAccountId ?? fallbackClearing;
            if (clearingAccountId is null || clearingAccountId.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Drop-ship item {candidate.ItemCode} has no Drop-ship Clearing account configured and the company has " +
                    "no account bound to system role 'drop_ship_clearing'. Seed CoA 21600 with system_role='drop_ship_clearing'.");
            }

            documentLines.Add(new InvoiceDropShipCogsPostingDocumentLine(
                lineNumber: ++lineNumber,
                itemId: candidate.ItemId,
                cogsAccountId: cogsAccountId.Value,
                dropShipClearingAccountId: clearingAccountId.Value,
                description: $"Drop-ship COGS for {candidate.ItemCode} on invoice {header.Value.InvoiceNumber}",
                amountBase: amountBase));
        }

        if (documentLines.Count == 0)
        {
            return new InvoiceDropShipCogsPostingPreparation(
                Document: null,
                ExistingJournalEntryId: null,
                ExistingJournalEntryDisplayNumber: null);
        }

        // Identifiers mirror the M3 SalesIssueCogs pattern: short JE
        // identifier derived from the invoice id so the GL display ties
        // back to the source document.
        var idShort = invoiceDocumentId.ToString("N")[..12].ToUpperInvariant();
        var entityNumber = EntityNumber.FromLegacy($"EN-DSCOGS-{idShort}");
        var displayNumber = new DocumentNumber($"DSCOGS-{idShort}");
        var baseCurrency = new CurrencyCode(header.Value.BaseCurrencyCode);

        var document = new InvoiceDropShipCogsPostingDocument(
            id: invoiceDocumentId,
            companyId: companyId,
            entityNumber: entityNumber,
            displayNumber: displayNumber,
            status: "draft",
            invoiceDocumentId: invoiceDocumentId,
            documentDate: header.Value.InvoiceDate,
            baseCurrencyCode: baseCurrency,
            lines: documentLines);

        return new InvoiceDropShipCogsPostingPreparation(
            Document: document,
            ExistingJournalEntryId: null,
            ExistingJournalEntryDisplayNumber: null);
    }

    private static async Task<(Guid Id, string DisplayNumber)?> TryReadExistingJournalAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id, display_number
            from journal_entries
            where company_id = @company_id
              and source_type = @source_type
              and source_id = @source_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", DropShipCogsSourceType);
        command.Parameters.AddWithValue("source_id", invoiceDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (reader.GetGuid(0), reader.GetString(1));
    }

    private static async Task<HeaderRow?> ReadHeaderAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select i.status, i.invoice_date, i.invoice_number, i.base_currency_code
            from invoices i
            where i.company_id = @company_id
              and i.id = @invoice_id;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_id", invoiceDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new HeaderRow(
            Status: reader.GetString(0),
            InvoiceDate: reader.GetFieldValue<DateOnly>(1),
            InvoiceNumber: reader.GetString(2),
            BaseCurrencyCode: reader.GetString(3).Trim().ToUpperInvariant());
    }

    private static async Task<List<LineCandidateRow>> ReadLineCandidatesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              i.id                                       as item_id,
              i.item_code                                as item_code,
              i.default_cogs_account_id                  as item_cogs_account_id,
              i.default_drop_ship_clearing_account_id    as item_drop_ship_clearing_account_id,
              i.default_purchase_price                   as purchase_price,
              sum(l.quantity)                            as quantity_sum
            from invoice_lines l
            join inventory_items i on i.id = l.item_id
            where l.company_id = @company_id
              and l.invoice_id = @invoice_id
              and i.item_kind = 'drop_ship'
            group by
              i.id,
              i.item_code,
              i.default_cogs_account_id,
              i.default_drop_ship_clearing_account_id,
              i.default_purchase_price
            having sum(l.quantity) > 0
            order by i.item_code;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_id", invoiceDocumentId);

        var rows = new List<LineCandidateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LineCandidateRow(
                ItemId: reader.GetGuid(0),
                ItemCode: reader.GetString(1),
                ItemCogsAccountId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                ItemDropShipClearingAccountId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                PurchasePrice: reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                QuantitySum: reader.GetDecimal(5)));
        }
        return rows;
    }

    private readonly record struct HeaderRow(
        string Status,
        DateOnly InvoiceDate,
        string InvoiceNumber,
        string BaseCurrencyCode);

    private readonly record struct LineCandidateRow(
        Guid ItemId,
        string ItemCode,
        Guid? ItemCogsAccountId,
        Guid? ItemDropShipClearingAccountId,
        decimal? PurchasePrice,
        decimal QuantitySum);
}
