using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresBillDocumentRepository : IBillDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresBillDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<BillDocument?> GetForPostingAsync(
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
        string billNumber;
        string status;
        DateOnly billDate;
        DateOnly dueDate;
        Guid vendorId;
        Guid payableAccountId;
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
                           b.id,
                           b.entity_number,
                           b.bill_number,
                           b.status,
                           b.bill_date,
                           b.due_date,
                           b.vendor_id,
                           b.document_currency_code,
                           b.base_currency_code,
                           b.fx_rate_snapshot_id,
                           b.fx_rate,
                           b.fx_requested_date,
                           b.fx_effective_date,
                           b.fx_source,
                           b.subtotal_amount,
                           b.tax_amount,
                           b.total_amount,
                           b.memo,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = b.company_id
                               and a.is_active = true
                               and (
                                 (b.document_currency_code = b.base_currency_code and (a.system_role = 'accounts_payable' or a.code = '2000'))
                                 or
                                 (b.document_currency_code <> b.base_currency_code and (a.system_role = ('accounts_payable:' || b.document_currency_code) or a.code = ('AP-' || b.document_currency_code)))
                               )
                             order by
                               case
                                 when b.document_currency_code = b.base_currency_code and a.system_role = 'accounts_payable' then 0
                                 when b.document_currency_code <> b.base_currency_code and a.system_role = ('accounts_payable:' || b.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as payable_account_id
                         from bills b
                         where b.company_id = @company_id
                           and b.id = @document_id
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
            billNumber = reader.GetString(reader.GetOrdinal("bill_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            billDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("bill_date"));
            dueDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date"));
            vendorId = reader.GetGuid(reader.GetOrdinal("vendor_id"));
            payableAccountId = reader.IsDBNull(reader.GetOrdinal("payable_account_id"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("payable_account_id"));
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

        if (payableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Bill routing could not resolve an active Accounts Payable control account.");
        }

        var lines = new List<BillDocumentLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.expense_account_id,
                           l.description,
                           l.line_amount,
                           l.tax_amount,
                           l.is_tax_recoverable,
                           tc.recoverable_account_id
                         from bill_lines l
                         left join tax_codes tc
                           on tc.id = l.tax_code_id
                          and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.bill_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var recoverableTaxAccountId = reader.IsDBNull(reader.GetOrdinal("recoverable_account_id"))
                    ? (Guid?)null
                    : reader.GetGuid(reader.GetOrdinal("recoverable_account_id"));

                lines.Add(new BillDocumentLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("expense_account_id")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    reader.GetBoolean(reader.GetOrdinal("is_tax_recoverable")),
                    recoverableTaxAccountId));
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

        return new BillDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(billNumber),
            status,
            billDate,
            dueDate,
            vendorId,
            payableAccountId,
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
