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
            "receive_payment" => await GetReceivePaymentDocumentAsync(
                scope,
                companyId,
                documentId,
                cancellationToken),
            "credit_application" => await GetCreditApplicationDocumentAsync(
                scope,
                companyId,
                documentId,
                cancellationToken),
            "pay_bill" => await GetPayBillDocumentAsync(
                scope,
                companyId,
                documentId,
                cancellationToken),
            "vendor_credit_application" => await GetVendorCreditApplicationDocumentAsync(
                scope,
                companyId,
                documentId,
                cancellationToken),
            _ => null
        };
    }

    public async Task<IReadOnlyList<AccountingSourceDocumentListItem>> ListSourceDocumentsAsync(
        CompanyId companyId,
        string? sourceType,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedSourceType = NormalizeSourceType(sourceType);
        var effectiveLimit = limit <= 0 ? 50 : Math.Min(limit, 200);

        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(
            """
            with source_documents as (
              select
                'invoice'::text as source_type,
                i.id,
                i.company_id,
                i.entity_number,
                i.invoice_number as display_number,
                i.status,
                i.invoice_date as document_date,
                i.due_date,
                'customer'::text as counterparty_role,
                i.customer_id as counterparty_id,
                c.display_name as counterparty_display_name,
                i.document_currency_code,
                i.base_currency_code,
                i.total_amount
              from invoices i
              inner join customers c
                on c.company_id = i.company_id
               and c.id = i.customer_id
              where i.company_id = @company_id

              union all

              select
                'credit_note'::text as source_type,
                cnote.id,
                cnote.company_id,
                cnote.entity_number,
                cnote.credit_note_number as display_number,
                cnote.status,
                cnote.credit_note_date as document_date,
                cnote.due_date,
                'customer'::text as counterparty_role,
                cnote.customer_id as counterparty_id,
                c.display_name as counterparty_display_name,
                cnote.document_currency_code,
                cnote.base_currency_code,
                cnote.total_amount
              from credit_notes cnote
              inner join customers c
                on c.company_id = cnote.company_id
               and c.id = cnote.customer_id
              where cnote.company_id = @company_id

              union all

              select
                'bill'::text as source_type,
                b.id,
                b.company_id,
                b.entity_number,
                b.bill_number as display_number,
                b.status,
                b.bill_date as document_date,
                b.due_date,
                'vendor'::text as counterparty_role,
                b.vendor_id as counterparty_id,
                v.display_name as counterparty_display_name,
                b.document_currency_code,
                b.base_currency_code,
                b.total_amount
              from bills b
              inner join vendors v
                on v.company_id = b.company_id
               and v.id = b.vendor_id
              where b.company_id = @company_id

              union all

              select
                'vendor_credit'::text as source_type,
                vc.id,
                vc.company_id,
                vc.entity_number,
                vc.vendor_credit_number as display_number,
                vc.status,
                vc.vendor_credit_date as document_date,
                vc.due_date,
                'vendor'::text as counterparty_role,
                vc.vendor_id as counterparty_id,
                v.display_name as counterparty_display_name,
                vc.document_currency_code,
                vc.base_currency_code,
                vc.total_amount
              from vendor_credits vc
              inner join vendors v
                on v.company_id = vc.company_id
               and v.id = vc.vendor_id
              where vc.company_id = @company_id
            )
            select
              source_type,
              id,
              entity_number,
              display_number,
              status,
              document_date,
              due_date,
              counterparty_role,
              counterparty_id,
              counterparty_display_name,
              document_currency_code,
              base_currency_code,
              total_amount
            from source_documents
            where @source_type is null
               or source_type = @source_type
            order by document_date desc, display_number asc
            limit @limit;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("source_type", (object?)normalizedSourceType ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", effectiveLimit);

        var items = new List<AccountingSourceDocumentListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AccountingSourceDocumentListItem(
                reader.GetString(reader.GetOrdinal("source_type")),
                reader.GetGuid(reader.GetOrdinal("id")),
                companyId,
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("display_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("document_date")),
                reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
                reader.GetString(reader.GetOrdinal("counterparty_role")),
                reader.IsDBNull(reader.GetOrdinal("counterparty_id")) ? null : reader.GetGuid(reader.GetOrdinal("counterparty_id")),
                reader.IsDBNull(reader.GetOrdinal("counterparty_display_name")) ? null : reader.GetString(reader.GetOrdinal("counterparty_display_name")),
                reader.GetString(reader.GetOrdinal("document_currency_code")),
                reader.GetString(reader.GetOrdinal("base_currency_code")),
                reader.GetDecimal(reader.GetOrdinal("total_amount"))));
        }

        return items;
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
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
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
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
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

    private static async Task<AccountingDocumentReview?> GetReceivePaymentDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "receive_payments",
            displayNumberColumn: "payment_number",
            dateColumn: "payment_date",
            counterpartyIdColumn: "customer_id",
            controlRole: "accounts_receivable",
            counterpartyRole: "customer",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadReceivePaymentLinesAsync(scope, companyId, documentId, cancellationToken);
        return BuildSettlementReview("receive_payment", companyId, header, "customer", lines);
    }

    private static async Task<AccountingDocumentReview?> GetCreditApplicationDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "credit_applications",
            displayNumberColumn: "application_number",
            dateColumn: "application_date",
            counterpartyIdColumn: "customer_id",
            controlRole: "accounts_receivable",
            counterpartyRole: "customer",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadCreditApplicationLinesAsync(scope, companyId, documentId, cancellationToken);
        return BuildSettlementReview("credit_application", companyId, header, "customer", lines);
    }

    private static async Task<AccountingDocumentReview?> GetPayBillDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "pay_bills",
            displayNumberColumn: "payment_number",
            dateColumn: "payment_date",
            counterpartyIdColumn: "vendor_id",
            controlRole: "accounts_payable",
            counterpartyRole: "vendor",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadPayBillLinesAsync(scope, companyId, documentId, cancellationToken);
        return BuildSettlementReview("pay_bill", companyId, header, "vendor", lines);
    }

    private static async Task<AccountingDocumentReview?> GetVendorCreditApplicationDocumentAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var header = await GetSettlementHeaderAsync(
            scope,
            companyId,
            documentId,
            tableName: "vendor_credit_applications",
            displayNumberColumn: "application_number",
            dateColumn: "application_date",
            counterpartyIdColumn: "vendor_id",
            controlRole: "accounts_payable",
            counterpartyRole: "vendor",
            cancellationToken);

        if (header is null)
        {
            return null;
        }

        var lines = await LoadVendorCreditApplicationLinesAsync(scope, companyId, documentId, cancellationToken);
        return BuildSettlementReview("vendor_credit_application", companyId, header, "vendor", lines);
    }

    private static AccountingDocumentReview BuildSettlementReview(
        string sourceType,
        CompanyId companyId,
        DocumentHeader header,
        string counterpartyRole,
        IReadOnlyList<AccountingDocumentReviewLine> lines) =>
        new(
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
            header.TotalAmount,
            0m,
            header.TotalAmount,
            header.Memo,
            lines);

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadReceivePaymentLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.target_ar_open_item_id,
              oi.source_type as target_source_type,
              oi.source_id as target_source_id,
              coalesce(i.invoice_number, cn.credit_note_number, oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from receive_payment_lines l
            inner join ar_open_items oi
              on oi.company_id = l.company_id
             and oi.id = l.target_ar_open_item_id
            left join invoices i
              on oi.source_type = 'invoice'
             and i.company_id = oi.company_id
             and i.id = oi.source_id
            left join credit_notes cn
              on oi.source_type = 'credit_note'
             and cn.company_id = oi.company_id
             and cn.id = oi.source_id
            where l.company_id = @company_id
              and l.receive_payment_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description = $"Applied to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("target_ar_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_source_type")),
                reader.GetGuid(reader.GetOrdinal("target_source_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadCreditApplicationLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.source_credit_ar_open_item_id,
              source_oi.source_type as source_document_type,
              source_oi.source_id as source_document_id,
              coalesce(cn.credit_note_number, source_oi.source_id::text) as source_display_number,
              l.target_invoice_ar_open_item_id,
              target_oi.source_type as target_document_type,
              target_oi.source_id as target_document_id,
              coalesce(i.invoice_number, target_oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from credit_application_lines l
            inner join ar_open_items source_oi
              on source_oi.company_id = l.company_id
             and source_oi.id = l.source_credit_ar_open_item_id
            inner join ar_open_items target_oi
              on target_oi.company_id = l.company_id
             and target_oi.id = l.target_invoice_ar_open_item_id
            left join credit_notes cn
              on source_oi.source_type = 'credit_note'
             and cn.company_id = source_oi.company_id
             and cn.id = source_oi.source_id
            left join invoices i
              on target_oi.source_type = 'invoice'
             and i.company_id = target_oi.company_id
             and i.id = target_oi.source_id
            where l.company_id = @company_id
              and l.credit_application_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description =
                $"Apply {reader.GetString(reader.GetOrdinal("source_display_number"))} to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("source_credit_ar_open_item_id")),
                reader.GetString(reader.GetOrdinal("source_document_type")),
                reader.GetGuid(reader.GetOrdinal("source_document_id")),
                reader.GetString(reader.GetOrdinal("source_display_number")),
                reader.GetGuid(reader.GetOrdinal("target_invoice_ar_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_document_type")),
                reader.GetGuid(reader.GetOrdinal("target_document_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadPayBillLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.target_ap_open_item_id,
              oi.source_type as target_source_type,
              oi.source_id as target_source_id,
              coalesce(b.bill_number, vc.vendor_credit_number, oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from pay_bill_lines l
            inner join ap_open_items oi
              on oi.company_id = l.company_id
             and oi.id = l.target_ap_open_item_id
            left join bills b
              on oi.source_type = 'bill'
             and b.company_id = oi.company_id
             and b.id = oi.source_id
            left join vendor_credits vc
              on oi.source_type = 'vendor_credit'
             and vc.company_id = oi.company_id
             and vc.id = oi.source_id
            where l.company_id = @company_id
              and l.pay_bill_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description = $"Applied to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("target_ap_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_source_type")),
                reader.GetGuid(reader.GetOrdinal("target_source_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
    }

    private static async Task<IReadOnlyList<AccountingDocumentReviewLine>> LoadVendorCreditApplicationLinesAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var lines = new List<AccountingDocumentReviewLine>();

        await using var command = scope.CreateCommand(
            """
            select
              l.line_number,
              l.source_vendor_credit_ap_open_item_id,
              source_oi.source_type as source_document_type,
              source_oi.source_id as source_document_id,
              coalesce(vc.vendor_credit_number, source_oi.source_id::text) as source_display_number,
              l.target_bill_ap_open_item_id,
              target_oi.source_type as target_document_type,
              target_oi.source_id as target_document_id,
              coalesce(b.bill_number, target_oi.source_id::text) as target_display_number,
              l.applied_amount_tx
            from vendor_credit_application_lines l
            inner join ap_open_items source_oi
              on source_oi.company_id = l.company_id
             and source_oi.id = l.source_vendor_credit_ap_open_item_id
            inner join ap_open_items target_oi
              on target_oi.company_id = l.company_id
             and target_oi.id = l.target_bill_ap_open_item_id
            left join vendor_credits vc
              on source_oi.source_type = 'vendor_credit'
             and vc.company_id = source_oi.company_id
             and vc.id = source_oi.source_id
            left join bills b
              on target_oi.source_type = 'bill'
             and b.company_id = target_oi.company_id
             and b.id = target_oi.source_id
            where l.company_id = @company_id
              and l.vendor_credit_application_id = @document_id
            order by l.line_number asc;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var description =
                $"Apply {reader.GetString(reader.GetOrdinal("source_display_number"))} to {reader.GetString(reader.GetOrdinal("target_display_number"))}";
            lines.Add(new AccountingDocumentReviewLine(
                reader.GetInt32(reader.GetOrdinal("line_number")),
                Guid.Empty,
                string.Empty,
                string.Empty,
                description,
                null,
                null,
                reader.GetDecimal(reader.GetOrdinal("applied_amount_tx")),
                0m,
                null,
                null,
                null,
                null,
                reader.GetGuid(reader.GetOrdinal("source_vendor_credit_ap_open_item_id")),
                reader.GetString(reader.GetOrdinal("source_document_type")),
                reader.GetGuid(reader.GetOrdinal("source_document_id")),
                reader.GetString(reader.GetOrdinal("source_display_number")),
                reader.GetGuid(reader.GetOrdinal("target_bill_ap_open_item_id")),
                reader.GetString(reader.GetOrdinal("target_document_type")),
                reader.GetGuid(reader.GetOrdinal("target_document_id")),
                reader.GetString(reader.GetOrdinal("target_display_number"))));
        }

        return lines;
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
                    txCredit,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
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
            counterpartyRole,
            counterpartyId,
            controlAccountId,
            transactionCurrencyCode,
            baseCurrencyCode,
            subtotalAmount,
            taxAmount,
            totalAmount,
            memo);
    }

    private static async Task<DocumentHeader?> GetSettlementHeaderAsync(
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
        Guid counterpartyId;
        string transactionCurrencyCode;
        string baseCurrencyCode;
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
                           d.{counterpartyIdColumn} as counterparty_id,
                           d.document_currency_code,
                           d.base_currency_code,
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
            counterpartyId = reader.GetGuid(reader.GetOrdinal("counterparty_id"));
            transactionCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
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
            null,
            counterpartyRole,
            counterpartyId,
            controlAccountId,
            transactionCurrencyCode,
            baseCurrencyCode,
            totalAmount,
            0m,
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
        string CounterpartyRole,
        Guid? CounterpartyId,
        Guid? ControlAccountId,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal SubtotalAmount,
        decimal TaxAmount,
        decimal TotalAmount,
        string? Memo);

    private static string? NormalizeSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            return null;
        }

        return sourceType.Trim().ToLowerInvariant() switch
        {
            "invoice" => "invoice",
            "credit_note" => "credit_note",
            "bill" => "bill",
            "vendor_credit" => "vendor_credit",
            _ => null
        };
    }
}
