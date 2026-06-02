using Citus.Accounting.Application;
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

public sealed class PostgresBillDocumentRepository : IBillDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;
    private readonly ISalesTaxEngine? _salesTaxEngine;
    private readonly ITaxSnapshotPersister? _taxSnapshotPersister;
    private readonly bool _salesTaxV2Enabled;

    public PostgresBillDocumentRepository(
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

    public async Task<BillDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        // Inventory-grade bill_lines columns are managed by the
        // migration runner; no inline ALTER on the read path.

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

        // B2 (S5.1): load the per-line tax snapshots once, attach to each
        // line below so the posting fragment builder can emit per-rule ITC /
        // non-recoverable legs. Empty when the document was saved with
        // SalesTaxV2 off (then the legacy single-leg path runs).
        var taxSnapshotsByLine = await PostgresLineTaxSnapshotLoader.LoadAsync(
            scope, companyId, "bill", documentId, cancellationToken);

        var lines = new List<BillDocumentLine>();

        await using (var linesCommand = scope.CreateCommand(
                         """
                         select
                           l.id,
                           l.line_number,
                           l.expense_account_id,
                           l.description,
                           l.line_amount,
                           l.item_id,
                           l.warehouse_id,
                           l.uom_code,
                           l.quantity,
                           l.unit_cost,
                           l.purchase_order_id,
                           l.purchase_order_line_number,
                           l.tax_amount,
                           l.is_tax_recoverable,
                           l.tax_code_id,
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
                var taxCodeId = reader.IsDBNull(reader.GetOrdinal("tax_code_id"))
                    ? (Guid?)null
                    : reader.GetGuid(reader.GetOrdinal("tax_code_id"));

                lines.Add(new BillDocumentLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("expense_account_id")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    reader.GetBoolean(reader.GetOrdinal("is_tax_recoverable")),
                    recoverableTaxAccountId,
                    taxCodeId,
                    reader.IsDBNull(reader.GetOrdinal("item_id")) ? null : reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.IsDBNull(reader.GetOrdinal("warehouse_id")) ? null : reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                    reader.IsDBNull(reader.GetOrdinal("uom_code")) ? null : reader.GetString(reader.GetOrdinal("uom_code")),
                    reader.IsDBNull(reader.GetOrdinal("quantity")) ? null : reader.GetDecimal(reader.GetOrdinal("quantity")),
                    reader.IsDBNull(reader.GetOrdinal("unit_cost")) ? null : reader.GetDecimal(reader.GetOrdinal("unit_cost")),
                    reader.IsDBNull(reader.GetOrdinal("purchase_order_id")) ? null : reader.GetGuid(reader.GetOrdinal("purchase_order_id")),
                    reader.IsDBNull(reader.GetOrdinal("purchase_order_line_number")) ? null : reader.GetInt32(reader.GetOrdinal("purchase_order_line_number")),
                    PostgresLineTaxSnapshotLoader.ForLine(taxSnapshotsByLine, reader.GetGuid(reader.GetOrdinal("id")))));
            }
        }

        // M6 iter 2: drop-ship line account override. For lines that
        // reference a drop-ship item, replace whatever expense_account_id
        // the user picked at draft time with the resolved Drop-ship Clearing
        // account: per-item override (inventory_items.default_drop_ship_clearing_account_id)
        // → company-level account with system_role = 'drop_ship_clearing'
        // (CoA code 21600). The override happens here (load-for-posting)
        // rather than at draft save so existing drafts pick up the right
        // routing automatically and the rule lives in one place. Iter 4
        // builds the matching workbench that reconciles these debits
        // against invoice-side credits.
        lines = await ApplyDropShipClearingOverrideAsync(scope, companyId, lines, cancellationToken);

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
            EntityNumber.Parse(entityNumber),
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

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        BillDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        // Stable id per draft line up front (the line table is
        // delete-then-reinsert), reused for the bill_lines row, the
        // engine line request, and the snapshot row.
        var orderedLines = draft.Lines
            .OrderBy(static line => line.LineNumber)
            .Select(static line => (Line: line, LineId: Guid.NewGuid()))
            .ToList();

        // Sales Tax v2 (S2.2): purchase side. When the flag is on the
        // engine is the authority for tax_amount (engine-absolute) — on
        // bills this OVERRIDES the operator-entered tax_amount — and its
        // per-component output (incl. recoverable / non-recoverable split)
        // is persisted to document_line_sales_tax_snapshots after commit.
        // When off, behaviour is unchanged: the client-sent line.TaxAmount
        // is used and no snapshots are written.
        var salesTaxActive = _salesTaxV2Enabled
            && _salesTaxEngine is not null
            && _taxSnapshotPersister is not null;

        SalesTaxComputationResult? taxResult = null;
        if (salesTaxActive)
        {
            taxResult = await _salesTaxEngine!.ComputeAsync(
                new SalesTaxComputationRequest(
                    draft.CompanyId.Value,
                    draft.BillDate,
                    draft.TransactionCurrencyCode.Trim().ToUpperInvariant(),
                    SalesTaxDocumentSide.Purchase,
                    orderedLines
                        .Select(static entry => new SalesTaxLineRequest(
                            entry.LineId,
                            Round6(entry.Line.LineAmount),
                            entry.Line.TaxCodeId,
                            entry.Line.TaxCodeSetId))
                        .ToList()),
                cancellationToken);
        }

        var resolvedTaxByLineId = orderedLines.ToDictionary(
            entry => entry.LineId,
            entry => taxResult is null
                ? entry.Line.TaxAmount
                : taxResult.Lines.FirstOrDefault(r => r.LineId == entry.LineId)?.TotalTaxAmount ?? 0m);
        var headerTaxAmount = Round6(resolvedTaxByLineId.Values.Sum());

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Inventory-grade bill_lines columns are managed by the
        // migration runner; no inline ALTER on this write path.

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string displayNumber;

        await ValidatePurchaseOrderBillDraftAnchorsAsync(
            connection,
            transaction,
            draft.CompanyId,
            draft.VendorId,
            documentId,
            draft.Lines
                .Where(static line => line.PurchaseOrderId.HasValue)
                .GroupBy(static line => (
                    PurchaseOrderId: line.PurchaseOrderId!.Value,
                    PurchaseOrderLineNumber: line.PurchaseOrderLineNumber!.Value,
                    ItemId: line.ItemId!.Value,
                    UomCode: line.UomCode!.Trim().ToUpperInvariant()))
                .Select(static group => new PurchaseOrderBillAnchorCandidate(
                    group.Key.PurchaseOrderId,
                    group.Key.PurchaseOrderLineNumber,
                    group.Key.ItemId,
                    group.Key.UomCode,
                    group.Sum(static line => line.Quantity!.Value)))
                .ToArray(),
            cancellationToken);

        if (draft.DocumentId is null)
        {
            var year = draft.BillDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                5,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(connection, transaction, draft.CompanyId, year, cancellationToken),
                cancellationToken);

            displayNumber = !string.IsNullOrWhiteSpace(draft.BillNumber)
                ? draft.BillNumber.Trim()
                : await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "bill-display",
                "BILL-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId,
                    "bills",
                    "bill_number",
                    "^BILL-[0-9]+$",
                    6,
                    cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into bills (
                  id,
                  company_id,
                  entity_number,
                  bill_number,
                  vendor_id,
                  status,
                  bill_date,
                  due_date,
                  document_currency_code,
                  base_currency_code,
                  fx_rate_snapshot_id,
                  fx_rate,
                  fx_requested_date,
                  fx_effective_date,
                  fx_source,
                  subtotal_amount,
                  tax_amount,
                  total_amount,
                  memo,
                  payment_term_id,
                  source_purchase_order_id,
                  source_purchase_order_number,
                  posted_at,
                  created_by_user_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @bill_number,
                  @vendor_id,
                  'draft',
                  @bill_date,
                  @due_date,
                  @document_currency_code,
                  @base_currency_code,
                  @fx_rate_snapshot_id,
                  @fx_rate,
                  @fx_requested_date,
                  @fx_effective_date,
                  @fx_source,
                  @subtotal_amount,
                  @tax_amount,
                  @total_amount,
                  @memo,
                  @payment_term_id,
                  @source_purchase_order_id,
                  @source_purchase_order_number,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, displayNumber, headerTaxAmount);
            try
            {
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505"
                && ex.ConstraintName == "bills_unique_company_bill_number")
            {
                // Friendly message instead of a raw 500 — the endpoint maps
                // InvalidOperationException to a 400 the form can render.
                throw new InvalidOperationException(
                    $"A bill numbered '{displayNumber}' already exists. Enter a different bill number.");
            }
        }
        else
        {
            (entityNumber, displayNumber) = await LoadIdentityAsync(connection, transaction, draft.CompanyId, documentId, cancellationToken);

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                update bills
                set vendor_id = @vendor_id,
                    bill_date = @bill_date,
                    due_date = @due_date,
                    document_currency_code = @document_currency_code,
                    base_currency_code = @base_currency_code,
                    fx_rate_snapshot_id = @fx_rate_snapshot_id,
                    fx_rate = @fx_rate,
                    fx_requested_date = @fx_requested_date,
                    fx_effective_date = @fx_effective_date,
                    fx_source = @fx_source,
                    subtotal_amount = @subtotal_amount,
                    tax_amount = @tax_amount,
                    total_amount = @total_amount,
                    memo = @memo,
                    payment_term_id = @payment_term_id,
                    source_purchase_order_id = @source_purchase_order_id,
                    source_purchase_order_number = @source_purchase_order_number,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft';
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, displayNumber, headerTaxAmount, includeIdentity: false);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException("The bill draft could not be updated. Only draft bills can be modified.");
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from bill_lines
                where company_id = @company_id
                  and bill_id = @document_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteCommand.Parameters.AddWithValue("document_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (line, lineId) in orderedLines)
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                """
                insert into bill_lines (
                  id,
                  company_id,
                  bill_id,
                  line_number,
                  expense_account_id,
                  description,
                  line_amount,
                  item_id,
                  warehouse_id,
                  uom_code,
                  quantity,
                  unit_cost,
                  purchase_order_id,
                  purchase_order_line_number,
                  tax_code_id,
                  tax_code_set_id,
                  tax_amount,
                  is_tax_recoverable,
                  task_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @bill_id,
                  @line_number,
                  @expense_account_id,
                  @description,
                  @line_amount,
                  @item_id,
                  @warehouse_id,
                  @uom_code,
                  @quantity,
                  @unit_cost,
                  @purchase_order_id,
                  @purchase_order_line_number,
                  @tax_code_id,
                  @tax_code_set_id,
                  @tax_amount,
                  @is_tax_recoverable,
                  @task_id,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", lineId);
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("bill_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("expense_account_id", line.ExpenseAccountId);
            insertLineCommand.Parameters.AddWithValue("description", (line.Description ?? string.Empty).Trim());
            insertLineCommand.Parameters.AddWithValue("line_amount", Round6(line.LineAmount));
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("item_id", NpgsqlDbType.Uuid) { TypedValue = line.ItemId });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("warehouse_id", NpgsqlDbType.Uuid) { TypedValue = line.WarehouseId });
            insertLineCommand.Parameters.AddWithValue("uom_code", string.IsNullOrWhiteSpace(line.UomCode) ? (object)DBNull.Value : line.UomCode.Trim().ToUpperInvariant());
            insertLineCommand.Parameters.Add(new NpgsqlParameter<decimal?>("quantity", NpgsqlDbType.Numeric) { TypedValue = line.Quantity.HasValue ? Round6(line.Quantity.Value) : null });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<decimal?>("unit_cost", NpgsqlDbType.Numeric) { TypedValue = line.UnitCost.HasValue ? Round6(line.UnitCost.Value) : null });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("purchase_order_id", NpgsqlDbType.Uuid) { TypedValue = line.PurchaseOrderId });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<int?>("purchase_order_line_number", NpgsqlDbType.Integer) { TypedValue = line.PurchaseOrderLineNumber });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("tax_code_id", NpgsqlDbType.Uuid) { TypedValue = line.TaxCodeId });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("tax_code_set_id", NpgsqlDbType.Uuid) { TypedValue = line.TaxCodeSetId });
            insertLineCommand.Parameters.AddWithValue("tax_amount", Round6(resolvedTaxByLineId[lineId]));
            insertLineCommand.Parameters.AddWithValue("is_tax_recoverable", line.IsTaxRecoverable);
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("task_id", NpgsqlDbType.Uuid) { TypedValue = line.TaskId });
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // Write the engine's snapshot rows after the lines commit. The
        // persister uses its own connection/transaction (S2.0 design), so
        // it must follow the commit; a draft re-save replaces the prior
        // snapshot rows for this (company, document).
        if (taxResult is not null)
        {
            SalesTaxComputationResult computed = taxResult;
            await _taxSnapshotPersister!.PersistAsync(
                draft.CompanyId.Value,
                "bill",
                documentId,
                orderedLines
                    .Select(entry => (entry.LineId, computed.Lines.First(r => r.LineId == entry.LineId)))
                    .ToList(),
                cancellationToken);
        }

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, "draft");
    }

    public async Task<SourceDocumentDraftSaveResult> SubmitDraftAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (entityNumber, displayNumber) = await LoadIdentityAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        await using var submitCommand = connection.CreateCommand();
        submitCommand.Transaction = transaction;
        submitCommand.CommandText =
            """
            update bills
            set status = 'submitted',
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = 'draft';
            """;
        submitCommand.Parameters.AddWithValue("document_id", documentId);
        submitCommand.Parameters.AddWithValue("company_id", companyId.Value);

        var affectedRows = await submitCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("Only draft bills can be submitted.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, "submitted");
    }

    public async Task<SourceDocumentDraftSaveResult> CancelSubmittedAsync(
        CompanyId companyId,
        UserId userId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (entityNumber, displayNumber) = await LoadIdentityAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        await using var cancelCommand = connection.CreateCommand();
        cancelCommand.Transaction = transaction;
        cancelCommand.CommandText =
            """
            update bills
            set status = 'cancelled',
                updated_at = now()
            where id = @document_id
              and company_id = @company_id
              and status = 'submitted';
            """;
        cancelCommand.Parameters.AddWithValue("document_id", documentId);
        cancelCommand.Parameters.AddWithValue("company_id", companyId.Value);

        var affectedRows = await cancelCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("Only submitted bills can be cancelled.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, "cancelled");
    }

    public async Task MarkReversedAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        // Conservative guard: only an unpaid posted bill flips to 'reversed'.
        // partially_paid / paid carry Pay Bills applications that the
        // compensation JE does not unwind, so they are not reversible here.
        await using var command = scope.CreateCommand(
            """
            update bills
               set status = 'reversed',
                   updated_at = now()
             where company_id = @company_id
               and id = @id
               and status = 'posted';
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", documentId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<BillDocumentLine>> ApplyDropShipClearingOverrideAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        List<BillDocumentLine> lines,
        CancellationToken cancellationToken)
    {
        // Collect distinct items referenced by lines. Skip the round-trip
        // entirely when no line carries an item id (the common case for
        // expense-style bills).
        var distinctItemIds = lines
            .Where(static l => l.ItemId.HasValue)
            .Select(static l => l.ItemId!.Value)
            .Distinct()
            .ToArray();
        if (distinctItemIds.Length == 0)
        {
            return lines;
        }

        // One round-trip pulls (kind, per-item-clearing-account) for every
        // referenced item. Non-drop-ship items come back too — we just skip
        // them when building the override map.
        var dropShipMap = new Dictionary<Guid, Guid?>();
        await using (var itemsCommand = scope.CreateCommand(
                         """
                         select id, item_kind, default_drop_ship_clearing_account_id
                         from inventory_items
                         where company_id = @company_id
                           and id = any(@item_ids);
                         """))
        {
            itemsCommand.Parameters.AddWithValue("company_id", companyId.Value);
            itemsCommand.Parameters.Add(new NpgsqlParameter("item_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = distinctItemIds
            });
            await using var reader = await itemsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(1);
                if (!string.Equals(kind, "drop_ship", StringComparison.Ordinal)) continue;
                dropShipMap[reader.GetGuid(0)] = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            }
        }

        if (dropShipMap.Count == 0)
        {
            return lines;
        }

        // Resolve the company-level fallback once — only if at least one
        // drop-ship line lacks a per-item override.
        Guid? companyDefault = null;
        var needsCompanyFallback = dropShipMap.Values.Any(static v => v is null);
        if (needsCompanyFallback)
        {
            companyDefault = await PostgresAccountLookup.TryResolveActiveAccountIdAsync(
                scope, companyId, cancellationToken,
                "drop_ship_clearing");
        }

        var rewritten = new List<BillDocumentLine>(lines.Count);
        foreach (var line in lines)
        {
            if (!line.ItemId.HasValue || !dropShipMap.TryGetValue(line.ItemId.Value, out var perItem))
            {
                rewritten.Add(line);
                continue;
            }

            var resolved = perItem ?? companyDefault;
            if (resolved is null || resolved.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Drop-ship bill line {line.LineNumber} references item {line.ItemId.Value:D} but no Drop-ship Clearing account is configured. " +
                    "Set the item's Drop-ship Clearing account, or seed the company-level account with system_role='drop_ship_clearing' (CoA code 21600).");
            }

            // Re-build the line with the overridden expense account. All
            // other fields stay as-is — itemId is preserved (used by the
            // M6 iter 4 aging workbench), warehouse/qty/cost stay null
            // (drop-ship lines never carry them), tax fields + snapshots
            // unchanged (the multi-leg posting needs the snapshots intact).
            rewritten.Add(new BillDocumentLine(
                line.LineNumber,
                resolved.Value,
                line.Description,
                line.LineAmount,
                line.TaxAmount,
                line.IsTaxRecoverable,
                line.RecoverableTaxAccountId,
                line.TaxCodeId,
                line.ItemId,
                line.WarehouseId,
                line.UomCode,
                line.Quantity,
                line.UnitCost,
                line.PurchaseOrderId,
                line.PurchaseOrderLineNumber,
                line.TaxSnapshots));
        }

        return rewritten;
    }

    private static void ValidateDraft(BillDraftSaveModel draft)
    {
        if (draft.VendorId == Guid.Empty)
        {
            throw new InvalidOperationException("Bill draft requires a vendor.");
        }

        if (draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Bill draft must contain at least one line.");
        }

        foreach (var line in draft.Lines)
        {
            // Line description is optional on a bill — many vendor invoices
            // carry only a category + amount per line.
            if (line.LineNumber <= 0 || line.ExpenseAccountId == Guid.Empty)
            {
                throw new InvalidOperationException("Bill draft lines must have a line number and an expense account.");
            }

            if (line.LineAmount <= 0m || line.TaxAmount < 0m)
            {
                throw new InvalidOperationException("Bill draft amounts must be positive and tax cannot be negative.");
            }

            var hasInventorySemantics =
                line.ItemId.HasValue ||
                line.WarehouseId.HasValue ||
                !string.IsNullOrWhiteSpace(line.UomCode) ||
                line.Quantity.HasValue ||
                line.UnitCost.HasValue ||
                line.PurchaseOrderId.HasValue ||
                line.PurchaseOrderLineNumber.HasValue;

            if (!hasInventorySemantics)
            {
                continue;
            }

            if (!line.ItemId.HasValue || line.ItemId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require an item.");
            }

            if (!line.WarehouseId.HasValue || line.WarehouseId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a warehouse.");
            }

            if (string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a UOM code.");
            }

            if (!line.Quantity.HasValue || line.Quantity.Value <= 0m)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a positive quantity.");
            }

            if (!line.UnitCost.HasValue || line.UnitCost.Value < 0m)
            {
                throw new InvalidOperationException("Inventory-grade bill lines require a non-negative unit cost.");
            }

            if (line.PurchaseOrderId.HasValue != line.PurchaseOrderLineNumber.HasValue)
            {
                throw new InvalidOperationException("PO-anchored bill lines require both purchase order id and purchase order line number.");
            }

            if (line.PurchaseOrderId.HasValue && line.PurchaseOrderId.Value == Guid.Empty)
            {
                throw new InvalidOperationException("PO-anchored bill lines require a valid purchase order id.");
            }

            if (line.PurchaseOrderLineNumber.HasValue && line.PurchaseOrderLineNumber.Value <= 0)
            {
                throw new InvalidOperationException("PO-anchored bill lines require a positive purchase order line number.");
            }
        }

        ValidateFx(draft.TransactionCurrencyCode, draft.BaseCurrencyCode, draft.FxRate);
    }

    private static void BindHeader(
        NpgsqlCommand command,
        BillDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string displayNumber,
        decimal taxAmount,
        bool includeIdentity = true)
    {
        var (fxRate, fxSource, fxRequestedDate, fxEffectiveDate) = ResolveFx(draft.TransactionCurrencyCode, draft.BaseCurrencyCode, draft.BillDate, draft.FxRate, draft.FxEffectiveDate, draft.FxSource);
        var subtotalAmount = Round6(draft.Lines.Sum(static line => line.LineAmount));
        var totalAmount = Round6(subtotalAmount + taxAmount);

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("bill_number", displayNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("vendor_id", draft.VendorId);
        command.Parameters.AddWithValue("bill_date", draft.BillDate);
        command.Parameters.AddWithValue("due_date", draft.DueDate);
        command.Parameters.AddWithValue("document_currency_code", draft.TransactionCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("base_currency_code", draft.BaseCurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter<Guid?>("fx_rate_snapshot_id", NpgsqlDbType.Uuid) { TypedValue = draft.FxSnapshotId });
        command.Parameters.AddWithValue("fx_rate", fxRate);
        command.Parameters.AddWithValue("fx_requested_date", fxRequestedDate);
        command.Parameters.AddWithValue("fx_effective_date", fxEffectiveDate);
        command.Parameters.AddWithValue("fx_source", fxSource);
        command.Parameters.AddWithValue("subtotal_amount", subtotalAmount);
        command.Parameters.AddWithValue("tax_amount", taxAmount);
        command.Parameters.AddWithValue("total_amount", totalAmount);
        command.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(draft.Memo) ? (object)DBNull.Value : draft.Memo.Trim());
        command.Parameters.Add(new NpgsqlParameter<Guid?>("payment_term_id", NpgsqlDbType.Uuid) { TypedValue = draft.PaymentTermId });
        command.Parameters.Add(new NpgsqlParameter<Guid?>("source_purchase_order_id", NpgsqlDbType.Uuid) { TypedValue = draft.SourcePurchaseOrderId });
        command.Parameters.AddWithValue("source_purchase_order_number", string.IsNullOrWhiteSpace(draft.SourcePurchaseOrderNumber) ? (object)DBNull.Value : draft.SourcePurchaseOrderNumber.Trim());
    }

    private static void ValidateFx(string transactionCurrencyCode, string baseCurrencyCode, decimal? fxRate)
    {
        if (!string.Equals(transactionCurrencyCode.Trim(), baseCurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (!fxRate.HasValue || fxRate.Value <= 0m))
        {
            throw new InvalidOperationException("Foreign-currency bill drafts must provide a positive FX rate.");
        }
    }

    private static (decimal FxRate, string FxSource, DateOnly FxRequestedDate, DateOnly FxEffectiveDate) ResolveFx(
        string transactionCurrencyCode,
        string baseCurrencyCode,
        DateOnly documentDate,
        decimal? fxRate,
        DateOnly? fxEffectiveDate,
        string? fxSource)
    {
        var sameCurrency = string.Equals(transactionCurrencyCode.Trim(), baseCurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase);
        return sameCurrency
            ? (1m, "identity", documentDate, fxEffectiveDate ?? documentDate)
            : (Math.Round(fxRate!.Value, 10, MidpointRounding.ToEven), string.IsNullOrWhiteSpace(fxSource) ? "manual" : fxSource.Trim(), documentDate, fxEffectiveDate ?? documentDate);
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static async Task ValidatePurchaseOrderBillDraftAnchorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid billVendorId,
        Guid billDocumentId,
        IReadOnlyCollection<PurchaseOrderBillAnchorCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        await EnsurePurchaseOrderAnchorTablesAvailableAsync(connection, transaction, cancellationToken);

        foreach (var candidate in candidates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                with posted_bill_quantity as (
                  select coalesce(sum(line.quantity), 0)::numeric(18,6) as posted_billed_quantity
                  from bill_lines line
                  join bills bill
                    on bill.company_id = line.company_id
                   and bill.id = line.bill_id
                  where line.company_id = @company_id
                    and line.bill_id <> @bill_id
                    and line.purchase_order_id = @purchase_order_id
                    and line.purchase_order_line_number = @purchase_order_line_number
                    and bill.status = 'posted'
                )
                select
                  po.status,
                  po.vendor_id,
                  po_line.item_id,
                  po_line.uom_code,
                  po_line.ordered_quantity,
                  coalesce(posted.posted_billed_quantity, 0)::numeric(18,6) as posted_billed_quantity
                from purchase_orders po
                join purchase_order_lines po_line
                  on po_line.company_id = po.company_id
                 and po_line.purchase_order_id = po.id
                 and po_line.line_number = @purchase_order_line_number
                cross join posted_bill_quantity posted
                where po.company_id = @company_id
                  and po.id = @purchase_order_id
                limit 1;
                """;
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("bill_id", billDocumentId);
            command.Parameters.AddWithValue("purchase_order_id", candidate.PurchaseOrderId);
            command.Parameters.AddWithValue("purchase_order_line_number", candidate.PurchaseOrderLineNumber);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("PO-anchored bill lines must reference an existing purchase order line in the active company context.");
            }

            PurchaseOrderAnchorPolicy.EnsureAllowsNewAnchor(reader.GetString(reader.GetOrdinal("status")));

            if (reader.GetGuid(reader.GetOrdinal("vendor_id")) != billVendorId)
            {
                throw new InvalidOperationException($"PO-anchored bill line {candidate.PurchaseOrderLineNumber} must use the same vendor as the purchase order.");
            }

            if (reader.GetGuid(reader.GetOrdinal("item_id")) != candidate.ItemId)
            {
                throw new InvalidOperationException($"PO-anchored bill line {candidate.PurchaseOrderLineNumber} must use the same item as the purchase order line.");
            }

            var poUom = reader.GetString(reader.GetOrdinal("uom_code")).Trim().ToUpperInvariant();
            if (!string.Equals(poUom, candidate.UomCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"PO-anchored bill line {candidate.PurchaseOrderLineNumber} must use the same stock UOM as the purchase order line.");
            }

            var orderedQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("ordered_quantity")));
            var postedBilledQuantity = Round6(reader.GetDecimal(reader.GetOrdinal("posted_billed_quantity")));
            if (Round6(postedBilledQuantity + candidate.Quantity) > orderedQuantity)
            {
                throw new InvalidOperationException($"PO-anchored bill quantity exceeds the ordered quantity for purchase order {candidate.PurchaseOrderId:D} line {candidate.PurchaseOrderLineNumber}.");
            }
        }
    }

    private static async Task EnsurePurchaseOrderAnchorTablesAvailableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select to_regclass('purchase_orders') is not null and to_regclass('purchase_order_lines') is not null;";
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("PO anchors require first-class purchase order tables to exist.");
        }
    }

    // EnsureInventoryGradeBillLineColumnsAsync used to live here.
    // The columns + index are now in
    // deploy/migrations/2026-05-08-bill-line-inventory-columns.sql,
    // applied at deploy time. The two read-path call sites that used
    // to invoke this helper no longer take an AccessExclusiveLock on
    // bill_lines.

    private static async Task<(string EntityNumber, string DisplayNumber)> LoadIdentityAsync(
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
            select entity_number, bill_number
            from bills
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The bill draft could not be found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("bill_number")));
    }

    private sealed record PurchaseOrderBillAnchorCandidate(
        Guid PurchaseOrderId,
        int PurchaseOrderLineNumber,
        Guid ItemId,
        string UomCode,
        decimal Quantity);
}
