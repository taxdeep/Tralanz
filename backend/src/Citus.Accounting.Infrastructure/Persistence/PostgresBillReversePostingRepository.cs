using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Postgres impl of <see cref="IBillReversePostingRepository"/>. Reads the
/// original bill JE (source_type='bill', source_id=billId) plus its
/// journal_entry_lines, then constructs a <see cref="BillReversePostingDocument"/>
/// with each line's debit/credit (and TxDebit/TxCredit) swapped. Reading the
/// canonical JE row gives the already-balanced amounts — including every
/// per-rule recoverable-tax (ITC) leg — so the compensation reverses the whole
/// posting exactly. Mirror of <see cref="PostgresInvoiceReversePostingRepository"/>.
/// </summary>
public sealed class PostgresBillReversePostingRepository : IBillReversePostingRepository
{
    private const string ForwardSourceType = "bill";
    private const string ReverseSourceType = "bill_reversal";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresBillReversePostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<BillReversePostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        if (billId == Guid.Empty)
        {
            throw new ArgumentException("Bill id is required.", nameof(billId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        // Idempotency: already-reversed bill returns the prior reverse JE.
        var existing = await TryReadExistingReverseJournalAsync(scope, companyId, billId, cancellationToken);
        if (existing is not null)
        {
            return new BillReversePostingPreparation(
                Document: null,
                ExistingJournalEntryId: existing.Value.Id,
                ExistingJournalEntryDisplayNumber: existing.Value.DisplayNumber);
        }

        var header = await ReadForwardHeaderAsync(scope, companyId, billId, cancellationToken);
        if (header is null)
        {
            throw new InvalidOperationException(
                $"Bill {billId:D} has no posted forward journal entry to reverse.");
        }

        var lines = await ReadForwardLinesAsync(scope, companyId, header.Value.JournalEntryId, cancellationToken);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"Forward journal entry {header.Value.JournalEntryId:D} for bill {billId:D} has no lines.");
        }

        // Flip each line: original Dr → reverse Cr, original Cr → reverse Dr.
        // PostingRole + SourceLineNumber roll forward so audit queries can
        // pair forward+reverse legs by (source_id, source_line).
        var reverseLines = new List<BillReversePostingDocumentLine>(lines.Count);
        var lineNumber = 1;
        foreach (var line in lines)
        {
            reverseLines.Add(new BillReversePostingDocumentLine(
                LineNumber: lineNumber++,
                AccountId: line.AccountId,
                TxDebit: line.TxCredit,
                TxCredit: line.TxDebit,
                Debit: line.Credit,
                Credit: line.Debit,
                Description: ToReverseDescription(line.Description),
                PostingRole: ToReversePostingRole(line.PostingRole),
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                SourceLineNumber: line.SourceLineNumber));
        }

        var idShort = billId.ToString("N")[..12].ToUpperInvariant();
        var ordinalSuffix = BitConverter.ToUInt32(billId.ToByteArray(), 0) % (EntityNumber.MaxOrdinal + 1);
        var entityNumber = EntityNumber.Create(header.Value.PostingDate.Year, ordinalSuffix);
        var displayNumber = new DocumentNumber($"BILLREV-{idShort}");

        var document = new BillReversePostingDocument(
            id: billId,
            companyId: companyId,
            entityNumber: entityNumber,
            displayNumber: displayNumber,
            billId: billId,
            documentDate: header.Value.PostingDate,
            transactionCurrencyCode: new CurrencyCode(header.Value.TransactionCurrencyCode),
            baseCurrencyCode: new CurrencyCode(header.Value.BaseCurrencyCode),
            fxRate: header.Value.ExchangeRate,
            lines: reverseLines);

        return new BillReversePostingPreparation(
            Document: document,
            ExistingJournalEntryId: null,
            ExistingJournalEntryDisplayNumber: null);
    }

    private static string ToReverseDescription(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return "Bill reversal";
        }
        if (original.StartsWith("Reversal of ", StringComparison.OrdinalIgnoreCase))
        {
            return original;
        }
        return $"Reversal of {original}";
    }

    private static string ToReversePostingRole(string? originalRole)
    {
        if (string.IsNullOrWhiteSpace(originalRole))
        {
            return "reverse:bill";
        }
        if (originalRole.StartsWith("reverse:", StringComparison.Ordinal))
        {
            return originalRole;
        }
        return "reverse:" + originalRole;
    }

    private static async Task<(Guid Id, string DisplayNumber)?> TryReadExistingReverseJournalAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid billId,
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
        command.Parameters.AddWithValue("source_type", ReverseSourceType);
        command.Parameters.AddWithValue("source_id", billId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (reader.GetGuid(0), reader.GetString(1));
    }

    private static async Task<ForwardHeaderRow?> ReadForwardHeaderAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select id, posted_at::date as posting_date,
                   transaction_currency_code, base_currency_code, exchange_rate
            from journal_entries
            where company_id = @company_id
              and source_type = @source_type
              and source_id = @source_id
              and status = 'posted'
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", ForwardSourceType);
        command.Parameters.AddWithValue("source_id", billId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ForwardHeaderRow(
            JournalEntryId: reader.GetGuid(0),
            PostingDate: reader.GetFieldValue<DateOnly>(1),
            TransactionCurrencyCode: reader.GetString(2),
            BaseCurrencyCode: reader.GetString(3),
            ExchangeRate: reader.GetDecimal(4));
    }

    private static async Task<List<ForwardLineRow>> ReadForwardLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid forwardJournalEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select line_number, account_id, description,
                   tx_debit, tx_credit, debit, credit,
                   control_role, posting_role, party_id, source_line_number
            from journal_entry_lines
            where company_id = @company_id
              and journal_entry_id = @journal_entry_id
            order by line_number;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", forwardJournalEntryId);

        var rows = new List<ForwardLineRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ForwardLineRow(
                LineNumber: reader.GetInt32(0),
                AccountId: reader.GetGuid(1),
                Description: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                TxDebit: reader.GetDecimal(3),
                TxCredit: reader.GetDecimal(4),
                Debit: reader.GetDecimal(5),
                Credit: reader.GetDecimal(6),
                ControlRole: reader.IsDBNull(7) ? null : reader.GetString(7),
                PostingRole: reader.IsDBNull(8) ? null : reader.GetString(8),
                PartyId: reader.IsDBNull(9) ? null : reader.GetGuid(9),
                SourceLineNumber: reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        }
        return rows;
    }

    private readonly record struct ForwardHeaderRow(
        Guid JournalEntryId,
        DateOnly PostingDate,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal ExchangeRate);

    private readonly record struct ForwardLineRow(
        int LineNumber,
        Guid AccountId,
        string Description,
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit,
        string? ControlRole,
        string? PostingRole,
        Guid? PartyId,
        int? SourceLineNumber);
}
