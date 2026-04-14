using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresAccountingDocumentReviewRepository : IAccountingDocumentReviewRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresAccountingDocumentReviewRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<AccountingDocumentReview?> GetSourceDocumentAsync(
        CompanyId companyId,
        string sourceType,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "manual_journal" => await GetManualJournalDocumentAsync(
                scope,
                companyId,
                documentId,
                cancellationToken),
            "invoice" => await GetReceivableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: "invoice",
                headerTable: "invoices",
                lineTable: "invoice_lines",
                displayNumberColumn: "invoice_number",
                dateColumn: "invoice_date",
                lineAccountColumn: "revenue_account_id",
                taxAccountColumn: "payable_account_id",
                counterpartyIdColumn: "customer_id",
                lineForeignKeyColumn: "invoice_id",
                counterpartyRole: "customer",
                controlRole: "accounts_receivable",
                includeQuantity: true,
                cancellationToken),
            "credit_note" => await GetReceivableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: "credit_note",
                headerTable: "credit_notes",
                lineTable: "credit_note_lines",
                displayNumberColumn: "credit_note_number",
                dateColumn: "credit_note_date",
                lineAccountColumn: "revenue_account_id",
                taxAccountColumn: "payable_account_id",
                counterpartyIdColumn: "customer_id",
                lineForeignKeyColumn: "credit_note_id",
                counterpartyRole: "customer",
                controlRole: "accounts_receivable",
                includeQuantity: true,
                cancellationToken),
            "bill" => await GetPayableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: "bill",
                headerTable: "bills",
                lineTable: "bill_lines",
                displayNumberColumn: "bill_number",
                dateColumn: "bill_date",
                lineAccountColumn: "expense_account_id",
                taxAccountColumn: "recoverable_account_id",
                counterpartyIdColumn: "vendor_id",
                lineForeignKeyColumn: "bill_id",
                counterpartyRole: "vendor",
                controlRole: "accounts_payable",
                cancellationToken),
            "vendor_credit" => await GetPayableDocumentAsync(
                scope,
                companyId,
                documentId,
                sourceType: "vendor_credit",
                headerTable: "vendor_credits",
                lineTable: "vendor_credit_lines",
                displayNumberColumn: "vendor_credit_number",
                dateColumn: "vendor_credit_date",
                lineAccountColumn: "expense_account_id",
                taxAccountColumn: "recoverable_account_id",
                counterpartyIdColumn: "vendor_id",
                lineForeignKeyColumn: "vendor_credit_id",
                counterpartyRole: "vendor",
                controlRole: "accounts_payable",
                cancellationToken),
            _ => null
        };
    }

    private static async Task<AccountingDocumentReview?> GetReceivableDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        string headerTable,
        string lineTable,
        string displayNumberColumn,
        string dateColumn,
        string lineAccountColumn,
        string taxAccountColumn,
        string counterpartyIdColumn,
        string lineForeignKeyColumn,
        string counterpartyRole,
        string controlRole,
        bool includeQuantity,
        CancellationToken cancellationToken)
    {
        var header = await GetHeaderAsync(
            scope,
            companyId,
            documentId,
            headerTable,
            displayNumberColumn,
            dateColumn,
            counterpartyIdColumn,
            controlRole,
            counterpartyRole,
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = new List<AccountingDocumentReviewLine>();

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           l.line_number,
                           l.{lineAccountColumn} as account_id,
                           a.code as account_code,
                           a.name as account_name,
                           l.description,
                           {(includeQuantity ? "l.quantity," : "null::numeric as quantity,")}
                           {(includeQuantity ? "l.unit_price," : "null::numeric as unit_price,")}
                           l.line_amount,
                           l.tax_amount,
                           null::boolean as is_tax_recoverable,
                           tc.{taxAccountColumn} as tax_account_id
                          from {lineTable} l
                          inner join accounts a
                            on a.company_id = l.company_id
                           and a.id = l.{lineAccountColumn}
                          left join tax_codes tc
                          on tc.id = l.tax_code_id
                         and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.{lineForeignKeyColumn} = @document_id
                         order by l.line_number asc;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new AccountingDocumentReviewLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.IsDBNull(reader.GetOrdinal("quantity")) ? null : reader.GetDecimal(reader.GetOrdinal("quantity")),
                    reader.IsDBNull(reader.GetOrdinal("unit_price")) ? null : reader.GetDecimal(reader.GetOrdinal("unit_price")),
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    null,
                    reader.IsDBNull(reader.GetOrdinal("tax_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("tax_account_id")),
                    null,
                    null));
            }
        }

        return new AccountingDocumentReview(
            sourceType,
            header.Id,
            companyId,
            header.EntityNumber,
            header.DisplayNumber,
            header.Status,
            header.DocumentDate,
            header.DueDate,
            counterpartyRole,
            header.CounterpartyId,
            header.ControlAccountId,
            header.TransactionCurrencyCode,
            header.BaseCurrencyCode,
            header.SubtotalAmount,
            header.TaxAmount,
            header.TotalAmount,
            header.Memo,
            lines);
    }

    private static async Task<AccountingDocumentReview?> GetPayableDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string sourceType,
        string headerTable,
        string lineTable,
        string displayNumberColumn,
        string dateColumn,
        string lineAccountColumn,
        string taxAccountColumn,
        string counterpartyIdColumn,
        string lineForeignKeyColumn,
        string counterpartyRole,
        string controlRole,
        CancellationToken cancellationToken)
    {
        var header = await GetHeaderAsync(
            scope,
            companyId,
            documentId,
            headerTable,
            displayNumberColumn,
            dateColumn,
            counterpartyIdColumn,
            controlRole,
            counterpartyRole,
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = new List<AccountingDocumentReviewLine>();

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           l.line_number,
                           l.{lineAccountColumn} as account_id,
                           a.code as account_code,
                           a.name as account_name,
                           l.description,
                           null::numeric as quantity,
                           null::numeric as unit_price,
                           l.line_amount,
                           l.tax_amount,
                           l.is_tax_recoverable,
                           tc.{taxAccountColumn} as tax_account_id
                          from {lineTable} l
                          inner join accounts a
                            on a.company_id = l.company_id
                           and a.id = l.{lineAccountColumn}
                          left join tax_codes tc
                          on tc.id = l.tax_code_id
                         and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.{lineForeignKeyColumn} = @document_id
                         order by l.line_number asc;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new AccountingDocumentReviewLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.GetString(reader.GetOrdinal("description")),
                    null,
                    null,
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    reader.GetBoolean(reader.GetOrdinal("is_tax_recoverable")),
                    reader.IsDBNull(reader.GetOrdinal("tax_account_id")) ? null : reader.GetGuid(reader.GetOrdinal("tax_account_id")),
                    null,
                    null));
            }
        }

        return new AccountingDocumentReview(
            sourceType,
            header.Id,
            companyId,
            header.EntityNumber,
            header.DisplayNumber,
            header.Status,
            header.DocumentDate,
            header.DueDate,
            counterpartyRole,
            header.CounterpartyId,
            header.ControlAccountId,
            header.TransactionCurrencyCode,
            header.BaseCurrencyCode,
            header.SubtotalAmount,
            header.TaxAmount,
            header.TotalAmount,
            header.Memo,
            lines);
    }

    private static async Task<AccountingDocumentReview?> GetManualJournalDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        Guid id;
        string entityNumber;
        string displayNumber;
        string status;
        DateOnly entryDate;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        string? memo;
        decimal totalTxDebit;
        decimal totalTxCredit;

        await using (var command = scope.CreateCommand(
                         """
                         select
                           d.id,
                           d.entity_number,
                           d.display_number,
                           d.status,
                           d.entry_date,
                           d.transaction_currency_code,
                           d.base_currency_code,
                           d.memo,
                           coalesce(sum(l.tx_debit), 0) as total_tx_debit,
                           coalesce(sum(l.tx_credit), 0) as total_tx_credit
                         from manual_journal_documents d
                         left join manual_journal_document_lines l
                           on l.company_id = d.company_id
                          and l.manual_journal_document_id = d.id
                         where d.company_id = @company_id
                           and d.id = @document_id
                         group by
                           d.id,
                           d.entity_number,
                           d.display_number,
                           d.status,
                           d.entry_date,
                           d.transaction_currency_code,
                           d.base_currency_code,
                           d.memo
                         limit 1;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            entryDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("entry_date"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("transaction_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"));
            totalTxDebit = reader.GetDecimal(reader.GetOrdinal("total_tx_debit"));
            totalTxCredit = reader.GetDecimal(reader.GetOrdinal("total_tx_credit"));
        }

        var lines = new List<AccountingDocumentReviewLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.account_id,
                           a.code as account_code,
                           a.name as account_name,
                           l.description,
                           l.tx_debit,
                           l.tx_credit
                         from manual_journal_document_lines l
                         inner join accounts a
                           on a.company_id = l.company_id
                          and a.id = l.account_id
                         where l.company_id = @company_id
                           and l.manual_journal_document_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var txDebit = reader.GetDecimal(reader.GetOrdinal("tx_debit"));
                var txCredit = reader.GetDecimal(reader.GetOrdinal("tx_credit"));

                lines.Add(new AccountingDocumentReviewLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("account_id")),
                    reader.GetString(reader.GetOrdinal("account_code")),
                    reader.GetString(reader.GetOrdinal("account_name")),
                    reader.IsDBNull(reader.GetOrdinal("description")) ? string.Empty : reader.GetString(reader.GetOrdinal("description")),
                    null,
                    null,
                    txDebit != 0m ? txDebit : txCredit,
                    0m,
                    null,
                    null,
                    txDebit,
                    txCredit));
            }
        }

        return new AccountingDocumentReview(
            "manual_journal",
            id,
            companyId,
            entityNumber,
            displayNumber,
            status,
            entryDate,
            null,
            "journal",
            null,
            null,
            transactionCurrencyCode,
            baseCurrencyCode,
            totalTxDebit,
            0m,
            totalTxCredit,
            memo,
            lines);
    }

    private static async Task<DocumentHeader?> GetHeaderAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        string tableName,
        string displayNumberColumn,
        string dateColumn,
        string counterpartyIdColumn,
        string controlRole,
        string counterpartyRole,
        CancellationToken cancellationToken)
    {
        Guid id;
        string entityNumber;
        string displayNumber;
        string status;
        DateOnly documentDate;
        DateOnly? dueDate;
        Guid counterpartyId;
        string transactionCurrencyCode;
        string baseCurrencyCode;
        decimal subtotalAmount;
        decimal taxAmount;
        decimal totalAmount;
        string? memo;

        await using (var command = scope.CreateCommand(
                         $"""
                         select
                           d.id,
                           d.entity_number,
                           d.{displayNumberColumn} as display_number,
                           d.status,
                           d.{dateColumn} as document_date,
                           d.due_date,
                           d.{counterpartyIdColumn} as counterparty_id,
                           d.document_currency_code,
                           d.base_currency_code,
                           d.subtotal_amount,
                           d.tax_amount,
                           d.total_amount,
                           d.memo
                         from {tableName} d
                         where d.company_id = @company_id
                           and d.id = @document_id
                         limit 1;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            displayNumber = reader.GetString(reader.GetOrdinal("display_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            documentDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date"));
            dueDate = reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date"));
            counterpartyId = reader.GetGuid(reader.GetOrdinal("counterparty_id"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            subtotalAmount = reader.GetDecimal(reader.GetOrdinal("subtotal_amount"));
            taxAmount = reader.GetDecimal(reader.GetOrdinal("tax_amount"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo")) ? null : reader.GetString(reader.GetOrdinal("memo"));
        }

        var controlAccountId = await PostgresControlAccountLookup.TryResolveAsync(
            scope,
            companyId.Value,
            controlRole,
            transactionCurrencyCode,
            baseCurrencyCode,
            cancellationToken);

        return new DocumentHeader(
            id,
            entityNumber,
            displayNumber,
            status,
            documentDate,
            dueDate,
            counterpartyId,
            controlAccountId,
            transactionCurrencyCode,
            baseCurrencyCode,
            subtotalAmount,
            taxAmount,
            totalAmount,
            memo);
    }

    private sealed record DocumentHeader(
        Guid Id,
        string EntityNumber,
        string DisplayNumber,
        string Status,
        DateOnly DocumentDate,
        DateOnly? DueDate,
        Guid? CounterpartyId,
        Guid? ControlAccountId,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal SubtotalAmount,
        decimal TaxAmount,
        decimal TotalAmount,
        string? Memo);
}
