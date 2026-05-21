using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// H1: Postgres impl of <see cref="IExpenseVoidPostingRepository"/>.
/// Reads the original Expense JE (source_type='expense', source_id=
/// expenseId) plus its journal_entry_lines, then constructs
/// <see cref="ExpenseVoidPostingDocument"/> with each line's debit/
/// credit (and TxDebit/TxCredit) swapped. The forward expense post
/// is currently hand-rolled in <c>PostgreSqlExpenseStore</c>; reading
/// the canonical JE row gives us the balanced amounts to compensate
/// without re-doing the allocation math.
/// </summary>
public sealed class PostgresExpenseVoidPostingRepository : IExpenseVoidPostingRepository
{
    private const string ForwardSourceType = "expense";
    private const string ReverseSourceType = "expense_void";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresExpenseVoidPostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<ExpenseVoidPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid expenseId,
        CancellationToken cancellationToken)
    {
        if (expenseId == Guid.Empty)
        {
            throw new ArgumentException("Expense id is required.", nameof(expenseId));
        }

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        // Idempotency: already-compensated expense returns the prior JE.
        var existing = await TryReadExistingReverseJournalAsync(scope, companyId, expenseId, cancellationToken);
        if (existing is not null)
        {
            return new ExpenseVoidPostingPreparation(
                Document: null,
                ExistingJournalEntryId: existing.Value.Id,
                ExistingJournalEntryDisplayNumber: existing.Value.DisplayNumber);
        }

        var header = await ReadForwardHeaderAsync(scope, companyId, expenseId, cancellationToken);
        if (header is null)
        {
            throw new InvalidOperationException(
                $"Expense {expenseId:D} has no posted forward journal entry to compensate.");
        }

        var lines = await ReadForwardLinesAsync(scope, companyId, header.Value.JournalEntryId, cancellationToken);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                $"Forward journal entry {header.Value.JournalEntryId:D} for expense {expenseId:D} has no lines.");
        }

        // Flip each line: original Dr → reverse Cr, original Cr → reverse Dr.
        // PostingRole and SourceLineNumber roll forward unchanged so audit
        // queries can pair forward+reverse legs by (source_id, source_line).
        var voidLines = new List<ExpenseVoidPostingDocumentLine>(lines.Count);
        var lineNumber = 1;
        foreach (var line in lines)
        {
            voidLines.Add(new ExpenseVoidPostingDocumentLine(
                LineNumber: lineNumber++,
                AccountId: line.AccountId,
                // Swap TX legs.
                TxDebit: line.TxCredit,
                TxCredit: line.TxDebit,
                // Swap base legs.
                Debit: line.Credit,
                Credit: line.Debit,
                Description: ToVoidDescription(line.Description, header.Value.ExpenseNumber),
                PostingRole: ToVoidPostingRole(line.PostingRole),
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                SourceLineNumber: line.SourceLineNumber));
        }

        var idShort = expenseId.ToString("N")[..12].ToUpperInvariant();
        var ordinalSuffix = BitConverter.ToUInt32(expenseId.ToByteArray(), 0) % (EntityNumber.MaxOrdinal + 1);
        var entityNumber = EntityNumber.Create(header.Value.PostingDate.Year, ordinalSuffix);
        var displayNumber = new DocumentNumber($"EXPVOID-{idShort}");

        var document = new ExpenseVoidPostingDocument(
            id: expenseId,
            companyId: companyId,
            entityNumber: entityNumber,
            displayNumber: displayNumber,
            expenseId: expenseId,
            expenseNumber: header.Value.ExpenseNumber,
            documentDate: header.Value.PostingDate,
            transactionCurrencyCode: new CurrencyCode(header.Value.TransactionCurrencyCode),
            baseCurrencyCode: new CurrencyCode(header.Value.BaseCurrencyCode),
            fxRate: header.Value.ExchangeRate,
            lines: voidLines);

        return new ExpenseVoidPostingPreparation(
            Document: document,
            ExistingJournalEntryId: null,
            ExistingJournalEntryDisplayNumber: null);
    }

    private static string ToVoidDescription(string original, string expenseNumber)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return $"Void of expense {expenseNumber}";
        }
        if (original.StartsWith("Void ", StringComparison.OrdinalIgnoreCase))
        {
            return original;
        }
        return $"Void of {original}";
    }

    private static string ToVoidPostingRole(string? originalRole)
    {
        if (string.IsNullOrWhiteSpace(originalRole))
        {
            return "void:expense";
        }
        if (originalRole.StartsWith("void:", StringComparison.Ordinal))
        {
            return originalRole;
        }
        if (string.Equals(originalRole, "control:payment_account", StringComparison.Ordinal))
        {
            return "void:payment_account";
        }
        if (originalRole.StartsWith("source_line:", StringComparison.Ordinal))
        {
            return "void:" + originalRole.Substring("source_line:".Length);
        }
        return "void:" + originalRole;
    }

    private static async Task<(Guid Id, string DisplayNumber)?> TryReadExistingReverseJournalAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid expenseId,
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
        command.Parameters.AddWithValue("source_id", expenseId);

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
        Guid expenseId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select je.id, e.expense_no, je.posted_at::date as posting_date,
                   je.transaction_currency_code, je.base_currency_code, je.exchange_rate
            from journal_entries je
            join expenses e on e.id = je.source_id and e.company_id = je.company_id
            where je.company_id = @company_id
              and je.source_type = @source_type
              and je.source_id = @source_id
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", ForwardSourceType);
        command.Parameters.AddWithValue("source_id", expenseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ForwardHeaderRow(
            JournalEntryId: reader.GetGuid(0),
            ExpenseNumber: reader.GetString(1),
            PostingDate: reader.GetFieldValue<DateOnly>(2),
            TransactionCurrencyCode: reader.GetString(3),
            BaseCurrencyCode: reader.GetString(4),
            ExchangeRate: reader.GetDecimal(5));
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
        string ExpenseNumber,
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
