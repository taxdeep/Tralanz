using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M3 iteration 1: prepares a <see cref="SalesIssueCogsPostingDocument"/>
/// straight from <c>inventory_layer_consumptions</c> rolled up by item.
/// No persisted bridge table yet — idempotency lives at the journal
/// layer via <c>(source_type='sales_issue_cogs', source_id=salesIssueId)</c>.
/// A future iteration may introduce a persisted bridge mirroring the
/// H.11/H.12 GR/IR pattern.
/// </summary>
public sealed class PostgresSalesIssueCogsPostingRepository : ISalesIssueCogsPostingRepository
{
    private const string CogsSourceType = "sales_issue_cogs";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresSalesIssueCogsPostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<SalesIssueCogsPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        Guid salesIssueDocumentId,
        CancellationToken cancellationToken)
    {
        if (salesIssueDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Sales-issue document id is required.", nameof(salesIssueDocumentId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections, _executionContextAccessor, cancellationToken);

        // Idempotency probe — if a JE with this source already exists, skip
        // building the doc; caller surfaces the existing JE id.
        var existing = await TryReadExistingJournalAsync(scope, companyId, salesIssueDocumentId, cancellationToken);
        if (existing is not null)
        {
            return new SalesIssueCogsPostingPreparation(
                Document: null,
                ExistingJournalEntryId: existing.Value.Id,
                ExistingJournalEntryDisplayNumber: existing.Value.DisplayNumber);
        }

        // Header — verify sales-issue exists, is posted, and capture the
        // base currency + posting date for the produced JE.
        var header = await ReadHeaderAsync(scope, companyId, salesIssueDocumentId, cancellationToken);
        if (header is null)
        {
            throw new InvalidOperationException(
                $"Sales issue {salesIssueDocumentId:D} was not found for company {companyId.Value:D}.");
        }
        if (!string.Equals(header.Value.Status, "posted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Sales issue {salesIssueDocumentId:D} must be posted before COGS can be journalised. Current status: {header.Value.Status}.");
        }

        // Body — sum cost-base per item across every consumption row that
        // belongs to a ledger entry of this sales-issue. JOIN inventory_items
        // to surface item-level COGS / Inventory account overrides; fall
        // back to company-level SystemRoles when item didn't pin one.
        var lines = await ReadLineCandidatesAsync(scope, companyId, salesIssueDocumentId, cancellationToken);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"Sales issue {salesIssueDocumentId:D} has no cost-layer consumption rows. Was it posted via the inventory engine?");
        }

        // Resolve company-level fallbacks once — same accounts apply to
        // every item that didn't pin its own.
        var fallbackCogs = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope, companyId, cancellationToken, "cost_of_goods_sold", "inventory:cogs");
        var fallbackInventory = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
            scope, companyId, cancellationToken, "inventory_asset", "inventory:asset");

        var documentLines = new List<SalesIssueCogsPostingDocumentLine>();
        var lineNumber = 0;
        foreach (var candidate in lines)
        {
            var cogsAccountId = candidate.ItemCogsAccountId ?? fallbackCogs;
            var inventoryAccountId = candidate.ItemInventoryAssetAccountId ?? fallbackInventory;
            if (cogsAccountId is null || cogsAccountId.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Item {candidate.ItemId:D} has no COGS account configured and the company has no account bound to system role 'cost_of_goods_sold'. Set 51000 Cost of Goods Sold's system role via the activation wizard.");
            }
            if (inventoryAccountId is null || inventoryAccountId.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Item {candidate.ItemId:D} has no Inventory Asset account configured and the company has no account bound to system role 'inventory_asset'. Set 14000 Inventory's system role via the activation wizard.");
            }
            if (candidate.AmountBase <= 0m)
            {
                continue;
            }

            documentLines.Add(new SalesIssueCogsPostingDocumentLine(
                lineNumber: ++lineNumber,
                itemId: candidate.ItemId,
                cogsAccountId: cogsAccountId.Value,
                inventoryAssetAccountId: inventoryAccountId.Value,
                description: $"COGS for {candidate.ItemCode} on sales-issue {header.Value.SourceDocumentNumber ?? salesIssueDocumentId.ToString("D")}",
                amountBase: candidate.AmountBase));
        }

        if (documentLines.Count == 0)
        {
            throw new InvalidOperationException(
                "All cost-layer consumption rows for this sales-issue net to zero — nothing to journalise.");
        }

        var idShort = salesIssueDocumentId.ToString("N")[..12].ToUpperInvariant();
        var entityNumber = EntityNumber.FromLegacy($"EN-COGS-{idShort}");
        var displayNumber = new DocumentNumber($"COGS-{idShort}");
        var baseCurrency = new CurrencyCode(header.Value.BaseCurrencyCode);

        var document = new SalesIssueCogsPostingDocument(
            id: salesIssueDocumentId,
            companyId: companyId,
            entityNumber: entityNumber,
            displayNumber: displayNumber,
            status: "draft",
            salesIssueDocumentId: salesIssueDocumentId,
            documentDate: header.Value.PostingDate,
            baseCurrencyCode: baseCurrency,
            lines: documentLines);

        return new SalesIssueCogsPostingPreparation(
            Document: document,
            ExistingJournalEntryId: null,
            ExistingJournalEntryDisplayNumber: null);
    }

    private static async Task<(Guid Id, string DisplayNumber)?> TryReadExistingJournalAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid salesIssueDocumentId,
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
        command.Parameters.AddWithValue("source_type", CogsSourceType);
        command.Parameters.AddWithValue("source_id", salesIssueDocumentId);

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
        Guid salesIssueDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select d.status, d.posting_date, d.source_document_number, c.base_currency_code
            from inventory_documents d
            join companies c on c.id = d.company_id
            where d.company_id = @company_id
              and d.id = @sales_issue_id
              and d.document_type = 'sales_issue';
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("sales_issue_id", salesIssueDocumentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new HeaderRow(
            Status: reader.GetString(0),
            PostingDate: reader.GetFieldValue<DateOnly>(1),
            SourceDocumentNumber: reader.IsDBNull(2) ? null : reader.GetString(2),
            BaseCurrencyCode: reader.GetString(3).Trim().ToUpperInvariant());
    }

    private static async Task<List<LineCandidateRow>> ReadLineCandidatesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid salesIssueDocumentId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select
              i.id              as item_id,
              i.item_code       as item_code,
              i.default_cogs_account_id            as item_cogs_account_id,
              i.default_inventory_asset_account_id as item_inventory_account_id,
              sum(c.consumed_cost_base)            as amount_base
            from inventory_layer_consumptions c
            join inventory_ledger_entries le on le.id = c.issue_ledger_entry_id
            join inventory_items i on i.id = le.item_id
            where c.company_id = @company_id
              and le.source_document_id = @sales_issue_id
            group by i.id, i.item_code, i.default_cogs_account_id, i.default_inventory_asset_account_id
            having sum(c.consumed_cost_base) > 0
            order by i.item_code;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("sales_issue_id", salesIssueDocumentId);

        var rows = new List<LineCandidateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LineCandidateRow(
                ItemId: reader.GetGuid(0),
                ItemCode: reader.GetString(1),
                ItemCogsAccountId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                ItemInventoryAssetAccountId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                AmountBase: reader.GetDecimal(4)));
        }
        return rows;
    }

    private readonly record struct HeaderRow(
        string Status,
        DateOnly PostingDate,
        string? SourceDocumentNumber,
        string BaseCurrencyCode);

    private readonly record struct LineCandidateRow(
        Guid ItemId,
        string ItemCode,
        Guid? ItemCogsAccountId,
        Guid? ItemInventoryAssetAccountId,
        decimal AmountBase);
}
