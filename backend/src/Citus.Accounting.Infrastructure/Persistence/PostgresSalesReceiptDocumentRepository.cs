using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Mirror of <see cref="PostgresInvoiceDocumentRepository"/> for the
/// cash-in-hand sale flow. Two structural deltas:
///   1. No <c>due_date</c> column / parameter — sales receipts settle
///      at the point of sale.
///   2. The repository owns <c>deposit_to_account_id</c>,
///      <c>payment_method</c>, and <c>reference_no</c> on the header,
///      replacing the invoice's <c>receivable_account_id</c> derivation.
///
/// Status transitions match the parent table CHECK: 'draft' → 'posted'
/// (via <c>PostgresJournalEntryWriter</c>'s sales_receipt source-type
/// case) → 'voided' / 'reversed' (future). No 'submitted' interim
/// state because sales receipts always save-and-post.
/// </summary>
public sealed class PostgresSalesReceiptDocumentRepository : ISalesReceiptDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresSalesReceiptDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<SalesReceiptDocument?> GetForPostingAsync(
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
        string receiptNumber;
        string status;
        DateOnly receiptDate;
        Guid customerId;
        Guid depositToAccountId;
        string paymentMethod;
        string? referenceNo;
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
        string? customerPoNumber;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           sr.id,
                           sr.entity_number,
                           sr.receipt_number,
                           sr.status,
                           sr.receipt_date,
                           sr.customer_id,
                           sr.deposit_to_account_id,
                           sr.payment_method,
                           sr.reference_no,
                           sr.document_currency_code,
                           sr.base_currency_code,
                           sr.fx_rate_snapshot_id,
                           sr.fx_rate,
                           sr.fx_requested_date,
                           sr.fx_effective_date,
                           sr.fx_source,
                           sr.subtotal_amount,
                           sr.tax_amount,
                           sr.total_amount,
                           sr.memo,
                           sr.customer_po_number
                         from sales_receipts sr
                         where sr.company_id = @company_id
                           and sr.id = @document_id
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
            receiptNumber = reader.GetString(reader.GetOrdinal("receipt_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            receiptDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date"));
            customerId = reader.GetGuid(reader.GetOrdinal("customer_id"));
            depositToAccountId = reader.GetGuid(reader.GetOrdinal("deposit_to_account_id"));
            paymentMethod = reader.GetString(reader.GetOrdinal("payment_method"));
            referenceNo = reader.IsDBNull(reader.GetOrdinal("reference_no"))
                ? null
                : reader.GetString(reader.GetOrdinal("reference_no"));
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
            customerPoNumber = reader.IsDBNull(reader.GetOrdinal("customer_po_number"))
                ? null
                : reader.GetString(reader.GetOrdinal("customer_po_number"));
        }

        var lines = new List<SalesReceiptDocumentLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.line_number,
                           l.revenue_account_id,
                           l.description,
                           l.quantity,
                           l.unit_price,
                           l.line_amount,
                           l.tax_code_id,
                           l.tax_amount,
                           tc.payable_account_id as tax_payable_account_id
                         from sales_receipt_lines l
                         left join tax_codes tc on tc.id = l.tax_code_id
                         where l.company_id = @company_id
                           and l.sales_receipt_id = @document_id
                         order by l.line_number asc;
                         """))
        {
            linesCommand.Parameters.AddWithValue("company_id", companyId.Value);
            linesCommand.Parameters.AddWithValue("document_id", documentId);

            await using var reader = await linesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var lineNumber = reader.GetInt32(reader.GetOrdinal("line_number"));
                var revenueAccountId = reader.GetGuid(reader.GetOrdinal("revenue_account_id"));
                var description = reader.GetString(reader.GetOrdinal("description"));
                var quantity = reader.GetDecimal(reader.GetOrdinal("quantity"));
                var unitPrice = reader.GetDecimal(reader.GetOrdinal("unit_price"));
                var lineAmount = reader.GetDecimal(reader.GetOrdinal("line_amount"));
                Guid? taxCodeId = reader.IsDBNull(reader.GetOrdinal("tax_code_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("tax_code_id"));
                var lineTaxAmount = reader.GetDecimal(reader.GetOrdinal("tax_amount"));
                Guid? payableTaxAccountId = reader.IsDBNull(reader.GetOrdinal("tax_payable_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("tax_payable_account_id"));

                lines.Add(new SalesReceiptDocumentLine(
                    lineNumber,
                    revenueAccountId,
                    description,
                    quantity,
                    unitPrice,
                    lineAmount,
                    lineTaxAmount,
                    payableTaxAccountId,
                    taxCodeId));
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

        return new SalesReceiptDocument(
            id,
            companyId,
            new EntityNumber(entityNumber),
            new DocumentNumber(receiptNumber),
            status,
            receiptDate,
            customerId,
            depositToAccountId,
            paymentMethod,
            referenceNo,
            transactionCurrency,
            baseCurrency,
            fxSnapshot,
            lines,
            subtotalAmount,
            taxAmount,
            totalAmount,
            memo,
            customerPoNumber);
    }

    public async Task<IReadOnlyList<SalesReceiptListItem>> ListAsync(
        CompanyId companyId,
        bool includeDrafts,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        await using var command = scope.CreateCommand(includeDrafts
            ? """
              select sr.id, sr.entity_number, sr.receipt_number, sr.status, sr.receipt_date,
                     sr.customer_id, sr.document_currency_code, sr.total_amount, sr.payment_method, sr.posted_at,
                     sr.customer_po_number
              from sales_receipts sr
              where sr.company_id = @company_id
              order by sr.receipt_date desc, sr.created_at desc
              limit 200;
              """
            : """
              select sr.id, sr.entity_number, sr.receipt_number, sr.status, sr.receipt_date,
                     sr.customer_id, sr.document_currency_code, sr.total_amount, sr.payment_method, sr.posted_at,
                     sr.customer_po_number
              from sales_receipts sr
              where sr.company_id = @company_id
                and sr.status <> 'draft'
              order by sr.receipt_date desc, sr.created_at desc
              limit 200;
              """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<SalesReceiptListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SalesReceiptListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("receipt_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("receipt_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("document_currency_code")),
                reader.GetDecimal(reader.GetOrdinal("total_amount")),
                reader.GetString(reader.GetOrdinal("payment_method")),
                reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("customer_po_number")) ? null : reader.GetString(reader.GetOrdinal("customer_po_number"))));
        }
        return rows;
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        SalesReceiptDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string receiptNumber;

        if (draft.DocumentId is null)
        {
            var year = draft.ReceiptDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                $"entity-number:all:{year}",
                $"EN{year}",
                8,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
                    connection, transaction, year, cancellationToken),
                cancellationToken);

            receiptNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId.Value,
                "sales-receipt-display",
                "SR-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId.Value,
                    "sales_receipts",
                    "receipt_number",
                    "^SR-[0-9]+$",
                    4,
                    cancellationToken),
                cancellationToken);

            // Pre-compute totals from the lines so the header always
            // reflects the truth at save time. The same numbers
            // re-derive on GetForPostingAsync, so a manual UPDATE never
            // gets to drift from the line shape.
            var subtotal = Round6(draft.Lines.Sum(l => l.Quantity * l.UnitPrice));
            var taxTotal = Round6(draft.Lines.Sum(l => l.TaxAmount));
            var total = Round6(subtotal + taxTotal);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into sales_receipts (
                  id,
                  company_id,
                  entity_number,
                  receipt_number,
                  customer_id,
                  status,
                  receipt_date,
                  document_currency_code,
                  base_currency_code,
                  fx_rate_snapshot_id,
                  fx_rate,
                  fx_requested_date,
                  fx_effective_date,
                  fx_source,
                  deposit_to_account_id,
                  payment_method,
                  reference_no,
                  subtotal_amount,
                  tax_amount,
                  total_amount,
                  memo,
                  customer_po_number,
                  posted_at,
                  created_by_user_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @receipt_number,
                  @customer_id,
                  'draft',
                  @receipt_date,
                  @document_currency_code,
                  @base_currency_code,
                  @fx_rate_snapshot_id,
                  @fx_rate,
                  @fx_requested_date,
                  @fx_effective_date,
                  @fx_source,
                  @deposit_to_account_id,
                  @payment_method,
                  @reference_no,
                  @subtotal_amount,
                  @tax_amount,
                  @total_amount,
                  @memo,
                  @customer_po_number,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, receiptNumber, subtotal, taxTotal, total);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, receiptNumber) = await LoadIdentityAsync(
                connection, transaction, draft.CompanyId.Value, documentId, cancellationToken);

            var subtotal = Round6(draft.Lines.Sum(l => l.Quantity * l.UnitPrice));
            var taxTotal = Round6(draft.Lines.Sum(l => l.TaxAmount));
            var total = Round6(subtotal + taxTotal);

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update sales_receipts
                set customer_id = @customer_id,
                    receipt_date = @receipt_date,
                    document_currency_code = @document_currency_code,
                    base_currency_code = @base_currency_code,
                    fx_rate_snapshot_id = @fx_rate_snapshot_id,
                    fx_rate = @fx_rate,
                    fx_requested_date = @fx_requested_date,
                    fx_effective_date = @fx_effective_date,
                    fx_source = @fx_source,
                    deposit_to_account_id = @deposit_to_account_id,
                    payment_method = @payment_method,
                    reference_no = @reference_no,
                    subtotal_amount = @subtotal_amount,
                    tax_amount = @tax_amount,
                    total_amount = @total_amount,
                    memo = @memo,
                    customer_po_number = @customer_po_number,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, receiptNumber, subtotal, taxTotal, total, includeIdentity: false);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    "The sales receipt draft could not be updated. Only draft sales receipts can be modified.");
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from sales_receipt_lines
                where company_id = @company_id
                  and sales_receipt_id = @sales_receipt_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteCommand.Parameters.AddWithValue("sales_receipt_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in draft.Lines.OrderBy(static line => line.LineNumber))
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                """
                insert into sales_receipt_lines (
                  id,
                  company_id,
                  sales_receipt_id,
                  line_number,
                  revenue_account_id,
                  description,
                  quantity,
                  unit_price,
                  line_amount,
                  tax_code_id,
                  tax_amount,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @sales_receipt_id,
                  @line_number,
                  @revenue_account_id,
                  @description,
                  @quantity,
                  @unit_price,
                  @line_amount,
                  @tax_code_id,
                  @tax_amount,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("sales_receipt_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("revenue_account_id", line.RevenueAccountId);
            insertLineCommand.Parameters.AddWithValue("description", line.Description.Trim());
            insertLineCommand.Parameters.AddWithValue("quantity", Round6(line.Quantity));
            insertLineCommand.Parameters.AddWithValue("unit_price", Round6(line.UnitPrice));
            insertLineCommand.Parameters.AddWithValue("line_amount", Round6(line.Quantity * line.UnitPrice));
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("tax_code_id", NpgsqlDbType.Uuid) { TypedValue = line.TaxCodeId });
            insertLineCommand.Parameters.AddWithValue("tax_amount", Round6(line.TaxAmount));
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, receiptNumber, "draft");
    }

    private static void ValidateDraft(SalesReceiptDraftSaveModel draft)
    {
        if (draft.CustomerId == Guid.Empty)
        {
            throw new InvalidOperationException("Sales receipt draft requires a customer id.");
        }

        if (draft.DepositToAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Sales receipt draft requires a deposit-to account id.");
        }

        if (string.IsNullOrWhiteSpace(draft.PaymentMethod))
        {
            throw new InvalidOperationException("Sales receipt draft requires a payment method.");
        }

        if (string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode) || string.IsNullOrWhiteSpace(draft.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Sales receipt draft requires both transaction and base currency codes.");
        }

        if (draft.Lines is null || draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Sales receipt draft must include at least one line.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in draft.Lines)
        {
            if (line.LineNumber <= 0)
            {
                throw new InvalidOperationException("Sales receipt line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNumber))
            {
                throw new InvalidOperationException($"Sales receipt line numbers must be unique; duplicate {line.LineNumber}.");
            }

            if (line.RevenueAccountId == Guid.Empty)
            {
                throw new InvalidOperationException($"Sales receipt line {line.LineNumber} is missing a revenue account.");
            }

            if (string.IsNullOrWhiteSpace(line.Description))
            {
                throw new InvalidOperationException($"Sales receipt line {line.LineNumber} is missing a description.");
            }

            if (line.Quantity <= 0m || line.UnitPrice < 0m || line.TaxAmount < 0m)
            {
                throw new InvalidOperationException($"Sales receipt line {line.LineNumber} has invalid amounts.");
            }
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        SalesReceiptDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string receiptNumber,
        decimal subtotal,
        decimal taxTotal,
        decimal total,
        bool includeIdentity = true)
    {
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("receipt_number", receiptNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        command.Parameters.AddWithValue("customer_id", draft.CustomerId);
        command.Parameters.AddWithValue("receipt_date", draft.ReceiptDate);
        command.Parameters.AddWithValue("document_currency_code", draft.TransactionCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid)
        {
            TypedValue = draft.FxSnapshotId
        });
        command.Parameters.AddWithValue("fx_rate", draft.FxRate ?? 1m);
        command.Parameters.AddWithValue("fx_requested_date", draft.ReceiptDate);
        command.Parameters.AddWithValue("fx_effective_date", draft.FxEffectiveDate ?? draft.ReceiptDate);
        command.Parameters.AddWithValue("fx_source", string.IsNullOrWhiteSpace(draft.FxSource) ? "identity" : draft.FxSource.Trim());
        command.Parameters.AddWithValue("deposit_to_account_id", draft.DepositToAccountId);
        command.Parameters.AddWithValue("payment_method", draft.PaymentMethod.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("reference_no", string.IsNullOrWhiteSpace(draft.ReferenceNo) ? (object)DBNull.Value : draft.ReferenceNo.Trim());
        command.Parameters.AddWithValue("subtotal_amount", subtotal);
        command.Parameters.AddWithValue("tax_amount", taxTotal);
        command.Parameters.AddWithValue("total_amount", total);
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo.Trim());
        command.Parameters.AddWithValue("customer_po_number", string.IsNullOrWhiteSpace(draft.CustomerPoNumber) ? (object)DBNull.Value : draft.CustomerPoNumber.Trim());
    }

    private static async Task<(string EntityNumber, string ReceiptNumber)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select entity_number, receipt_number
            from sales_receipts
            where id = @id and company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Sales receipt draft not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("receipt_number")));
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
