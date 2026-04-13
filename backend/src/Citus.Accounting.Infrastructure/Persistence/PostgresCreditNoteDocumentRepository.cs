using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresCreditNoteDocumentRepository : ICreditNoteDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresCreditNoteDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<CreditNoteDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        Guid id;
        string entityNumber;
        string creditNoteNumber;
        string status;
        DateOnly creditNoteDate;
        DateOnly dueDate;
        Guid customerId;
        Guid receivableAccountId;
        string documentCurrencyCode;
        string baseCurrencyCode;
        Guid? fxSnapshotId;
        decimal fxRate;
        DateOnly fxRequestedDate;
        DateOnly fxEffectiveDate;
        string fxSource;
        decimal subtotalAmount;
        decimal taxAmount;
        decimal totalAmount;
        string? memo;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           c.id,
                           c.entity_number,
                           c.credit_note_number,
                           c.status,
                           c.credit_note_date,
                           c.due_date,
                           c.customer_id,
                           c.document_currency_code,
                           c.base_currency_code,
                           c.fx_rate_snapshot_id,
                           c.fx_rate,
                           c.fx_requested_date,
                           c.fx_effective_date,
                           c.fx_source,
                           c.subtotal_amount,
                           c.tax_amount,
                           c.total_amount,
                           c.memo,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = c.company_id
                               and a.is_active = true
                               and (
                                 (c.document_currency_code = c.base_currency_code and (a.system_role = 'accounts_receivable' or a.code = '1100'))
                                 or
                                 (c.document_currency_code <> c.base_currency_code and (a.system_role = ('accounts_receivable:' || c.document_currency_code) or a.code = ('AR-' || c.document_currency_code)))
                               )
                             order by
                               case
                                 when c.document_currency_code = c.base_currency_code and a.system_role = 'accounts_receivable' then 0
                                 when c.document_currency_code <> c.base_currency_code and a.system_role = ('accounts_receivable:' || c.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as receivable_account_id
                         from credit_notes c
                         where c.company_id = @company_id
                           and c.id = @document_id
                         limit 1;
                         """))
        {
            headerCommand.Parameters.AddWithValue("company_id", companyId.Value);
            headerCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(reader.GetOrdinal("id"));
            entityNumber = reader.GetString(reader.GetOrdinal("entity_number"));
            creditNoteNumber = reader.GetString(reader.GetOrdinal("credit_note_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            creditNoteDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("credit_note_date"));
            dueDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date"));
            customerId = reader.GetGuid(reader.GetOrdinal("customer_id"));
            receivableAccountId = reader.IsDBNull(reader.GetOrdinal("receivable_account_id"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("receivable_account_id"));
            documentCurrencyCode = reader.GetString(reader.GetOrdinal("document_currency_code"));
            baseCurrencyCode = reader.GetString(reader.GetOrdinal("base_currency_code"));
            fxSnapshotId = reader.IsDBNull(reader.GetOrdinal("fx_rate_snapshot_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("fx_rate_snapshot_id"));
            fxRate = reader.GetDecimal(reader.GetOrdinal("fx_rate"));
            fxRequestedDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_requested_date"));
            fxEffectiveDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("fx_effective_date"));
            fxSource = reader.GetString(reader.GetOrdinal("fx_source"));
            subtotalAmount = reader.GetDecimal(reader.GetOrdinal("subtotal_amount"));
            taxAmount = reader.GetDecimal(reader.GetOrdinal("tax_amount"));
            totalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount"));
            memo = reader.IsDBNull(reader.GetOrdinal("memo"))
                ? null
                : reader.GetString(reader.GetOrdinal("memo"));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Credit note routing could not resolve an active Accounts Receivable control account.");
        }

        var lines = new List<CreditNoteDocumentLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.revenue_account_id,
                           l.description,
                           l.quantity,
                           l.unit_price,
                           l.line_amount,
                           l.tax_amount,
                           tc.payable_account_id
                         from credit_note_lines l
                         left join tax_codes tc
                           on tc.id = l.tax_code_id
                          and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.credit_note_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var taxPayableAccountId = reader.IsDBNull(reader.GetOrdinal("payable_account_id"))
                    ? (Guid?)null
                    : reader.GetGuid(reader.GetOrdinal("payable_account_id"));

                lines.Add(new CreditNoteDocumentLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("revenue_account_id")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetDecimal(reader.GetOrdinal("quantity")),
                    reader.GetDecimal(reader.GetOrdinal("unit_price")),
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    taxPayableAccountId));
            }
        }

        var transactionCurrency = new CurrencyCode(documentCurrencyCode);
        var baseCurrency = new CurrencyCode(baseCurrencyCode);
        FxSnapshotRef? fxSnapshot = null;

        if (fxSnapshotId.HasValue || transactionCurrency != baseCurrency || fxRate != 1m)
        {
            fxSnapshot = new FxSnapshotRef(
                fxSnapshotId ?? Guid.Empty,
                baseCurrency,
                transactionCurrency,
                fxRate,
                fxRequestedDate,
                fxEffectiveDate,
                fxSource);
        }

        return new CreditNoteDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(creditNoteNumber),
            status,
            creditNoteDate,
            dueDate,
            customerId,
            receivableAccountId,
            transactionCurrency,
            baseCurrency,
            fxSnapshot,
            lines,
            subtotalAmount,
            taxAmount,
            totalAmount,
            memo);
    }
}
