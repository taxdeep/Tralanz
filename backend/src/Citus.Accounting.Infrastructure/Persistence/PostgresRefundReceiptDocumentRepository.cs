using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Modules.SalesTax.Application.Contracts;
using Citus.Modules.SalesTax.Domain.Shared;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// Refund-receipt repository. Persistence shape is the polarity flip of
/// <see cref="PostgresSalesReceiptDocumentRepository"/>:
///   • Deposit-to-account becomes refund-from-account on the header
///     (the asset row that loses the cash, not gains it).
///   • Adds a Reason column so the operator's "why refund" annotation
///     (return / pricing / RMA #) is queryable later, separate from
///     the free-form memo.
/// Same status set ('draft' → 'posted' → 'voided' / 'reversed') and
/// same numbering envelope (EN-prefixed entity number, RR- prefixed
/// receipt number).
/// </summary>
public sealed class PostgresRefundReceiptDocumentRepository : IRefundReceiptDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;
    private readonly ISalesTaxEngine? _salesTaxEngine;
    private readonly ITaxSnapshotPersister? _taxSnapshotPersister;
    private readonly bool _salesTaxV2Enabled;

    public PostgresRefundReceiptDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor,
        ISalesTaxEngine? salesTaxEngine = null,
        ITaxSnapshotPersister? taxSnapshotPersister = null,
        IOptions<SalesTaxV2Options>? salesTaxV2Options = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
        _salesTaxEngine = salesTaxEngine;
        _taxSnapshotPersister = taxSnapshotPersister;
        _salesTaxV2Enabled = salesTaxV2Options?.Value.Enabled ?? false;
    }

    public async Task<RefundReceiptDocument?> GetForPostingAsync(
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
        string refundNumber;
        string status;
        DateOnly refundDate;
        Guid customerId;
        Guid refundFromAccountId;
        string paymentMethod;
        string? referenceNo;
        string? reason;
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
                           rr.id,
                           rr.entity_number,
                           rr.refund_number,
                           rr.status,
                           rr.refund_date,
                           rr.customer_id,
                           rr.refund_from_account_id,
                           rr.payment_method,
                           rr.reference_no,
                           rr.reason,
                           rr.document_currency_code,
                           rr.base_currency_code,
                           rr.fx_rate_snapshot_id,
                           rr.fx_rate,
                           rr.fx_requested_date,
                           rr.fx_effective_date,
                           rr.fx_source,
                           rr.subtotal_amount,
                           rr.tax_amount,
                           rr.total_amount,
                           rr.memo,
                           rr.customer_po_number
                         from refund_receipts rr
                         where rr.company_id = @company_id
                           and rr.id = @document_id
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
            refundNumber = reader.GetString(reader.GetOrdinal("refund_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            refundDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("refund_date"));
            customerId = reader.GetGuid(reader.GetOrdinal("customer_id"));
            refundFromAccountId = reader.GetGuid(reader.GetOrdinal("refund_from_account_id"));
            paymentMethod = reader.GetString(reader.GetOrdinal("payment_method"));
            referenceNo = reader.IsDBNull(reader.GetOrdinal("reference_no"))
                ? null
                : reader.GetString(reader.GetOrdinal("reference_no"));
            reason = reader.IsDBNull(reader.GetOrdinal("reason"))
                ? null
                : reader.GetString(reader.GetOrdinal("reason"));
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

        var lines = new List<RefundReceiptDocumentLine>();

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
                         from refund_receipt_lines l
                         left join tax_codes tc on tc.id = l.tax_code_id
                         where l.company_id = @company_id
                           and l.refund_receipt_id = @document_id
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

                lines.Add(new RefundReceiptDocumentLine(
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

        return new RefundReceiptDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(refundNumber),
            status,
            refundDate,
            customerId,
            refundFromAccountId,
            paymentMethod,
            referenceNo,
            reason,
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

    public async Task<IReadOnlyList<RefundReceiptListItem>> ListAsync(
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
              select rr.id, rr.entity_number, rr.refund_number, rr.status, rr.refund_date,
                     rr.customer_id, rr.document_currency_code, rr.total_amount, rr.payment_method, rr.posted_at,
                     rr.customer_po_number
              from refund_receipts rr
              where rr.company_id = @company_id
              order by rr.refund_date desc, rr.created_at desc
              limit 200;
              """
            : """
              select rr.id, rr.entity_number, rr.refund_number, rr.status, rr.refund_date,
                     rr.customer_id, rr.document_currency_code, rr.total_amount, rr.payment_method, rr.posted_at,
                     rr.customer_po_number
              from refund_receipts rr
              where rr.company_id = @company_id
                and rr.status <> 'draft'
              order by rr.refund_date desc, rr.created_at desc
              limit 200;
              """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<RefundReceiptListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RefundReceiptListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("refund_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("refund_date")),
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
        RefundReceiptDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        // Stable id per draft line up front (the line table is
        // delete-then-reinsert), reused for the line row, the engine line
        // request, and the snapshot row.
        var orderedLines = draft.Lines
            .OrderBy(static line => line.LineNumber)
            .Select(static line => (Line: line, LineId: Guid.NewGuid()))
            .ToList();

        // Sales Tax v2 (S2.3): refund receipt is the sales side (a cash-out
        // reversal of a sale). When the flag is on the engine is the
        // authority for tax (engine-absolute); its output is persisted to
        // document_line_sales_tax_snapshots after commit. When off,
        // behaviour is unchanged.
        var salesTaxActive = _salesTaxV2Enabled
            && _salesTaxEngine is not null
            && _taxSnapshotPersister is not null;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        SalesTaxComputationResult? taxResult = null;
        if (salesTaxActive)
        {
            // load money_decimals (2 or 3, default 2)
            int moneyDecimals;
            await using (var mdCmd = connection.CreateCommand())
            {
                mdCmd.Transaction = transaction;
                mdCmd.CommandText = "select money_decimals from companies where id = @cid;";
                mdCmd.Parameters.AddWithValue("cid", draft.CompanyId.Value);
                var raw = await mdCmd.ExecuteScalarAsync(cancellationToken);
                var d = raw is null or DBNull ? 2 : Convert.ToInt32(raw);
                moneyDecimals = d is 2 or 3 ? d : 2;
            }

            taxResult = await _salesTaxEngine!.ComputeAsync(
                new SalesTaxComputationRequest(
                    draft.CompanyId.Value,
                    draft.RefundDate,
                    draft.TransactionCurrencyCode.Trim().ToUpperInvariant(),
                    SalesTaxDocumentSide.Sales,
                    orderedLines
                        .Select(static entry => new SalesTaxLineRequest(
                            entry.LineId,
                            Round6(entry.Line.Quantity * entry.Line.UnitPrice),
                            entry.Line.TaxCodeId))
                        .ToList(),
                    MoneyDecimals: moneyDecimals),
                cancellationToken);
        }

        var resolvedTaxByLineId = orderedLines.ToDictionary(
            entry => entry.LineId,
            entry => taxResult is null
                ? entry.Line.TaxAmount
                : taxResult.Lines.FirstOrDefault(r => r.LineId == entry.LineId)?.TotalTaxAmount ?? 0m);
        var headerTaxAmount = Round6(resolvedTaxByLineId.Values.Sum());

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string refundNumber;

        if (draft.DocumentId is null)
        {
            var year = draft.RefundDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                5,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
                    connection, transaction, draft.CompanyId, year, cancellationToken),
                cancellationToken);

            refundNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "refund-receipt-display",
                "RR-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId,
                    "refund_receipts",
                    "refund_number",
                    "^RR-[0-9]+$",
                    4,
                    cancellationToken),
                cancellationToken);

            var subtotal = Round6(draft.Lines.Sum(l => l.Quantity * l.UnitPrice));
            var taxTotal = headerTaxAmount;
            var total = Round6(subtotal + taxTotal);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into refund_receipts (
                  id,
                  company_id,
                  entity_number,
                  refund_number,
                  customer_id,
                  status,
                  refund_date,
                  document_currency_code,
                  base_currency_code,
                  fx_rate_snapshot_id,
                  fx_rate,
                  fx_requested_date,
                  fx_effective_date,
                  fx_source,
                  refund_from_account_id,
                  payment_method,
                  reference_no,
                  reason,
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
                  @refund_number,
                  @customer_id,
                  'draft',
                  @refund_date,
                  @document_currency_code,
                  @base_currency_code,
                  @fx_rate_snapshot_id,
                  @fx_rate,
                  @fx_requested_date,
                  @fx_effective_date,
                  @fx_source,
                  @refund_from_account_id,
                  @payment_method,
                  @reference_no,
                  @reason,
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
            BindHeader(insertCommand, draft, documentId, entityNumber, refundNumber, subtotal, taxTotal, total);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, refundNumber) = await LoadIdentityAsync(
                connection, transaction, draft.CompanyId, documentId, cancellationToken);

            var subtotal = Round6(draft.Lines.Sum(l => l.Quantity * l.UnitPrice));
            var taxTotal = headerTaxAmount;
            var total = Round6(subtotal + taxTotal);

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update refund_receipts
                set customer_id = @customer_id,
                    refund_date = @refund_date,
                    document_currency_code = @document_currency_code,
                    base_currency_code = @base_currency_code,
                    fx_rate_snapshot_id = @fx_rate_snapshot_id,
                    fx_rate = @fx_rate,
                    fx_requested_date = @fx_requested_date,
                    fx_effective_date = @fx_effective_date,
                    fx_source = @fx_source,
                    refund_from_account_id = @refund_from_account_id,
                    payment_method = @payment_method,
                    reference_no = @reference_no,
                    reason = @reason,
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
            BindHeader(updateCommand, draft, documentId, entityNumber, refundNumber, subtotal, taxTotal, total, includeIdentity: false);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    "The refund receipt draft could not be updated. Only draft refund receipts can be modified.");
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from refund_receipt_lines
                where company_id = @company_id
                  and refund_receipt_id = @refund_receipt_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteCommand.Parameters.AddWithValue("refund_receipt_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (line, lineId) in orderedLines)
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                """
                insert into refund_receipt_lines (
                  id,
                  company_id,
                  refund_receipt_id,
                  line_number,
                  revenue_account_id,
                  description,
                  quantity,
                  unit_price,
                  line_amount,
                  tax_code_id,
                  tax_amount,
                  task_id,
                  task_line_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @refund_receipt_id,
                  @line_number,
                  @revenue_account_id,
                  @description,
                  @quantity,
                  @unit_price,
                  @line_amount,
                  @tax_code_id,
                  @tax_amount,
                  @task_id,
                  @task_line_id,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", lineId);
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("refund_receipt_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("revenue_account_id", line.RevenueAccountId);
            insertLineCommand.Parameters.AddWithValue("description", line.Description.Trim());
            insertLineCommand.Parameters.AddWithValue("quantity", Round6(line.Quantity));
            insertLineCommand.Parameters.AddWithValue("unit_price", Round6(line.UnitPrice));
            insertLineCommand.Parameters.AddWithValue("line_amount", Round6(line.Quantity * line.UnitPrice));
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("tax_code_id", NpgsqlDbType.Uuid) { TypedValue = line.TaxCodeId });
            insertLineCommand.Parameters.AddWithValue("tax_amount", Round6(resolvedTaxByLineId[lineId]));
            // H6-3 (D8): refund receipt now persists task back-links
            // so the post handler can release the matching task_lines
            // (mirror of credit-note's reversal of invoice billing).
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("task_id", NpgsqlDbType.Uuid) { TypedValue = line.TaskId });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("task_line_id", NpgsqlDbType.Uuid) { TypedValue = line.TaskLineId });
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // Persist the engine's snapshot rows after the lines commit (the
        // persister has its own connection/transaction).
        if (taxResult is not null)
        {
            SalesTaxComputationResult computed = taxResult;
            await _taxSnapshotPersister!.PersistAsync(
                draft.CompanyId.Value,
                "refund_receipt",
                documentId,
                orderedLines
                    .Select(entry => (entry.LineId, computed.Lines.First(r => r.LineId == entry.LineId)))
                    .ToList(),
                cancellationToken);
        }

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, refundNumber, "draft");
    }

    public async Task<IReadOnlyList<RefundReceiptLineTaskLink>> ListLinkedTaskLineMappingsAsync(
        CompanyId companyId,
        Guid refundReceiptId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, task_id, task_line_id
            from refund_receipt_lines
            where company_id = @company_id
              and refund_receipt_id = @refund_receipt_id
              and task_id is not null
            order by line_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("refund_receipt_id", refundReceiptId);

        var rows = new List<RefundReceiptLineTaskLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RefundReceiptLineTaskLink(
                RefundReceiptLineId: reader.GetGuid(0),
                TaskId: reader.GetGuid(1),
                TaskLineId: reader.IsDBNull(2) ? null : reader.GetGuid(2)));
        }
        return rows;
    }

    private static void ValidateDraft(RefundReceiptDraftSaveModel draft)
    {
        if (draft.CustomerId == Guid.Empty)
        {
            throw new InvalidOperationException("Refund receipt draft requires a customer id.");
        }

        if (draft.RefundFromAccountId == Guid.Empty)
        {
            throw new InvalidOperationException("Refund receipt draft requires a refund-from account id.");
        }

        if (string.IsNullOrWhiteSpace(draft.PaymentMethod))
        {
            throw new InvalidOperationException("Refund receipt draft requires a payment method.");
        }

        if (string.IsNullOrWhiteSpace(draft.TransactionCurrencyCode) || string.IsNullOrWhiteSpace(draft.BaseCurrencyCode))
        {
            throw new InvalidOperationException("Refund receipt draft requires both transaction and base currency codes.");
        }

        if (draft.Lines is null || draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Refund receipt draft must include at least one line.");
        }

        var seenLineNumbers = new HashSet<int>();
        foreach (var line in draft.Lines)
        {
            if (line.LineNumber <= 0)
            {
                throw new InvalidOperationException("Refund receipt line numbers must be positive.");
            }

            if (!seenLineNumbers.Add(line.LineNumber))
            {
                throw new InvalidOperationException($"Refund receipt line numbers must be unique; duplicate {line.LineNumber}.");
            }

            if (line.RevenueAccountId == Guid.Empty)
            {
                throw new InvalidOperationException($"Refund receipt line {line.LineNumber} is missing a revenue account.");
            }

            if (string.IsNullOrWhiteSpace(line.Description))
            {
                throw new InvalidOperationException($"Refund receipt line {line.LineNumber} is missing a description.");
            }

            if (line.Quantity <= 0m || line.UnitPrice < 0m || line.TaxAmount < 0m)
            {
                throw new InvalidOperationException($"Refund receipt line {line.LineNumber} has invalid amounts.");
            }
        }
    }

    private static void BindHeader(
        NpgsqlCommand command,
        RefundReceiptDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string refundNumber,
        decimal subtotal,
        decimal taxTotal,
        decimal total,
        bool includeIdentity = true)
    {
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("refund_number", refundNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        command.Parameters.AddWithValue("customer_id", draft.CustomerId);
        command.Parameters.AddWithValue("refund_date", draft.RefundDate);
        command.Parameters.AddWithValue("document_currency_code", draft.TransactionCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid)
        {
            TypedValue = draft.FxSnapshotId
        });
        command.Parameters.AddWithValue("fx_rate", draft.FxRate ?? 1m);
        command.Parameters.AddWithValue("fx_requested_date", draft.RefundDate);
        command.Parameters.AddWithValue("fx_effective_date", draft.FxEffectiveDate ?? draft.RefundDate);
        command.Parameters.AddWithValue("fx_source", string.IsNullOrWhiteSpace(draft.FxSource) ? "identity" : draft.FxSource.Trim());
        command.Parameters.AddWithValue("refund_from_account_id", draft.RefundFromAccountId);
        command.Parameters.AddWithValue("payment_method", draft.PaymentMethod.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("reference_no", string.IsNullOrWhiteSpace(draft.ReferenceNo) ? (object)DBNull.Value : draft.ReferenceNo.Trim());
        command.Parameters.AddWithValue("reason", string.IsNullOrWhiteSpace(draft.Reason) ? (object)DBNull.Value : draft.Reason.Trim());
        command.Parameters.AddWithValue("subtotal_amount", subtotal);
        command.Parameters.AddWithValue("tax_amount", taxTotal);
        command.Parameters.AddWithValue("total_amount", total);
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo.Trim());
        command.Parameters.AddWithValue("customer_po_number", string.IsNullOrWhiteSpace(draft.CustomerPoNumber) ? (object)DBNull.Value : draft.CustomerPoNumber.Trim());
    }

    private static async Task<(string EntityNumber, string RefundNumber)> LoadIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select entity_number, refund_number
            from refund_receipts
            where id = @id and company_id = @company_id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Refund receipt draft not found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("refund_number")));
    }

    private static decimal Round6(decimal value) => Math.Round(value, 6, MidpointRounding.ToEven);
}
