using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresInvoiceDocumentRepository : IInvoiceDocumentRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresInvoiceDocumentRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<InvoiceDocument?> GetForPostingAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        // Schema for item_id / warehouse_id / uom_code on invoice_lines
        // is applied via deploy/migrations/2026-05-08-invoice-line-
        // inventory-columns.sql. No inline ALTER on the read path.

        Guid id;
        string entityNumber;
        string invoiceNumber;
        string status;
        DateOnly invoiceDate;
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
        string? customerPoNumber;
        Guid? salesOrderId;

        await using (var headerCommand = scope.CreateCommand(
                         """
                         select
                           i.id,
                           i.entity_number,
                           i.invoice_number,
                           i.status,
                           i.invoice_date,
                           i.due_date,
                           i.customer_id,
                           i.document_currency_code,
                           i.base_currency_code,
                           i.fx_rate_snapshot_id,
                           i.fx_rate,
                           i.fx_requested_date,
                           i.fx_effective_date,
                           i.fx_source,
                           i.subtotal_amount,
                           i.tax_amount,
                           i.total_amount,
                           i.memo,
                           i.customer_po_number,
                           i.sales_order_id,
                           (
                             select a.id
                             from accounts a
                             where a.company_id = i.company_id
                               and a.is_active = true
                               and (
                                 (i.document_currency_code = i.base_currency_code and (a.system_role = 'accounts_receivable' or a.code = '1100'))
                                 or
                                 (i.document_currency_code <> i.base_currency_code and (a.system_role = ('accounts_receivable:' || i.document_currency_code) or a.code = ('AR-' || i.document_currency_code)))
                               )
                             order by
                               case
                                 when i.document_currency_code = i.base_currency_code and a.system_role = 'accounts_receivable' then 0
                                 when i.document_currency_code <> i.base_currency_code and a.system_role = ('accounts_receivable:' || i.document_currency_code) then 0
                                 else 1
                               end,
                               a.code
                             limit 1
                           ) as receivable_account_id
                         from invoices i
                         where i.company_id = @company_id
                           and i.id = @document_id
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
            invoiceNumber = reader.GetString(reader.GetOrdinal("invoice_number"));
            status = reader.GetString(reader.GetOrdinal("status"));
            invoiceDate = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("invoice_date"));
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
            customerPoNumber = reader.IsDBNull(reader.GetOrdinal("customer_po_number"))
                ? null
                : reader.GetString(reader.GetOrdinal("customer_po_number"));
            salesOrderId = reader.IsDBNull(reader.GetOrdinal("sales_order_id"))
                ? null
                : reader.GetGuid(reader.GetOrdinal("sales_order_id"));
        }

        if (receivableAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Invoice routing could not resolve an active Accounts Receivable control account.");
        }

        var lines = new List<InvoiceDocumentLine>();

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
                           l.tax_code_id,
                           tc.payable_account_id,
                           l.item_id,
                           l.warehouse_id,
                           l.uom_code
                         from invoice_lines l
                         left join tax_codes tc
                           on tc.id = l.tax_code_id
                          and tc.company_id = l.company_id
                         where l.company_id = @company_id
                           and l.invoice_id = @document_id
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
                var taxCodeId = reader.IsDBNull(reader.GetOrdinal("tax_code_id"))
                    ? (Guid?)null
                    : reader.GetGuid(reader.GetOrdinal("tax_code_id"));

                lines.Add(new InvoiceDocumentLine(
                    reader.GetInt32(reader.GetOrdinal("line_number")),
                    reader.GetGuid(reader.GetOrdinal("revenue_account_id")),
                    reader.GetString(reader.GetOrdinal("description")),
                    reader.GetDecimal(reader.GetOrdinal("quantity")),
                    reader.GetDecimal(reader.GetOrdinal("unit_price")),
                    reader.GetDecimal(reader.GetOrdinal("line_amount")),
                    reader.GetDecimal(reader.GetOrdinal("tax_amount")),
                    taxPayableAccountId,
                    taxCodeId,
                    reader.IsDBNull(reader.GetOrdinal("item_id")) ? null : reader.GetGuid(reader.GetOrdinal("item_id")),
                    reader.IsDBNull(reader.GetOrdinal("warehouse_id")) ? null : reader.GetGuid(reader.GetOrdinal("warehouse_id")),
                    reader.IsDBNull(reader.GetOrdinal("uom_code")) ? null : reader.GetString(reader.GetOrdinal("uom_code"))));
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

        return new InvoiceDocument(
            id,
            companyId,
            EntityNumber.Parse(entityNumber),
            new DocumentNumber(invoiceNumber),
            status,
            invoiceDate,
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
            memo,
            customerPoNumber,
            salesOrderId);
    }

    public async Task<IReadOnlyList<InvoiceListItem>> ListAsync(
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
              select i.id, i.entity_number, i.invoice_number, i.status, i.invoice_date, i.due_date,
                     i.customer_id, coalesce(c.display_name, '') as customer_name,
                     i.document_currency_code, i.total_amount, i.posted_at,
                     i.customer_po_number, i.sales_order_id
              from invoices i
              left join customers c on c.company_id = i.company_id and c.id = i.customer_id
              where i.company_id = @company_id
              order by i.invoice_date desc, i.created_at desc
              limit 200;
              """
            : """
              select i.id, i.entity_number, i.invoice_number, i.status, i.invoice_date, i.due_date,
                     i.customer_id, coalesce(c.display_name, '') as customer_name,
                     i.document_currency_code, i.total_amount, i.posted_at,
                     i.customer_po_number, i.sales_order_id
              from invoices i
              left join customers c on c.company_id = i.company_id and c.id = i.customer_id
              where i.company_id = @company_id
                and i.status <> 'draft'
              order by i.invoice_date desc, i.created_at desc
              limit 200;
              """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = new List<InvoiceListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InvoiceListItem(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("entity_number")),
                reader.GetString(reader.GetOrdinal("invoice_number")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("invoice_date")),
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("due_date")),
                reader.GetGuid(reader.GetOrdinal("customer_id")),
                reader.GetString(reader.GetOrdinal("customer_name")),
                reader.GetString(reader.GetOrdinal("document_currency_code")),
                reader.GetDecimal(reader.GetOrdinal("total_amount")),
                reader.IsDBNull(reader.GetOrdinal("posted_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("posted_at")),
                reader.IsDBNull(reader.GetOrdinal("customer_po_number")) ? null : reader.GetString(reader.GetOrdinal("customer_po_number")),
                reader.IsDBNull(reader.GetOrdinal("sales_order_id")) ? null : reader.GetGuid(reader.GetOrdinal("sales_order_id"))));
        }
        return rows;
    }

    public async Task<SourceDocumentDraftSaveResult> SaveDraftAsync(
        InvoiceDraftSaveModel draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Inventory-grade invoice_lines columns are managed by the
        // migration runner (see 2026-05-08-invoice-line-inventory-
        // columns.sql); no inline ALTER on this write path.

        var documentId = draft.DocumentId ?? Guid.NewGuid();
        string entityNumber;
        string displayNumber;

        if (draft.DocumentId is null)
        {
            var year = draft.InvoiceDate.Year;
            entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                $"entity-number:all:{year}",
                $"EN{year}",
                5,
                await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(connection, transaction, draft.CompanyId, year, cancellationToken),
                cancellationToken);

            displayNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
                connection,
                transaction,
                draft.CompanyId,
                "invoice-display",
                "INV-",
                6,
                await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                    connection,
                    transaction,
                    draft.CompanyId,
                    "invoices",
                    "invoice_number",
                    "^INV-[0-9]+$",
                    5,
                    cancellationToken),
                cancellationToken);

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                insert into invoices (
                  id,
                  company_id,
                  entity_number,
                  invoice_number,
                  customer_id,
                  status,
                  invoice_date,
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
                  customer_po_number,
                  sales_order_id,
                  posted_at,
                  created_by_user_id,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @entity_number,
                  @invoice_number,
                  @customer_id,
                  'draft',
                  @invoice_date,
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
                  @customer_po_number,
                  @sales_order_id,
                  null,
                  @created_by_user_id,
                  now(),
                  now()
                );
                """;
            BindHeader(insertCommand, draft, documentId, entityNumber, displayNumber);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            (entityNumber, displayNumber) = await LoadIdentityAsync(connection, transaction, draft.CompanyId, documentId, cancellationToken);

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            // Optimistic-concurrency guard: the @expected_updated_at
            // parameter, when non-null, narrows the UPDATE to rows
            // that haven't moved since the caller's last GET. Zero
            // rows affected can mean any of (a) wrong company, (b) row
            // is no longer in 'draft' status, (c) timestamp drifted.
            // The follow-up SELECT below distinguishes (c) so we can
            // raise ConcurrencyConflictException instead of the
            // generic "could not be updated" error.
            updateCommand.CommandText =
                """
                update invoices
                set customer_id = @customer_id,
                    invoice_date = @invoice_date,
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
                    customer_po_number = @customer_po_number,
                    sales_order_id = @sales_order_id,
                    updated_at = now()
                where id = @id
                  and company_id = @company_id
                  and status = 'draft'
                  and (cast(@expected_updated_at as timestamptz) is null
                       or updated_at = cast(@expected_updated_at as timestamptz));
                """;
            BindHeader(updateCommand, draft, documentId, entityNumber, displayNumber, includeIdentity: false);
            updateCommand.Parameters.AddWithValue(
                "expected_updated_at",
                draft.ExpectedUpdatedAt.HasValue
                    ? (object)draft.ExpectedUpdatedAt.Value
                    : DBNull.Value);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows != 1)
            {
                if (draft.ExpectedUpdatedAt.HasValue &&
                    await DraftStillExistsAsync(connection, transaction, draft.CompanyId, documentId, cancellationToken))
                {
                    throw new ConcurrencyConflictException(
                        "This invoice draft was modified by another session after you opened it. " +
                        "Reload the draft to see the latest changes, then re-apply your edits.");
                }
                throw new InvalidOperationException("The invoice draft could not be updated. Only draft invoices can be modified.");
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                delete from invoice_lines
                where company_id = @company_id
                  and invoice_id = @invoice_id;
                """;
            deleteCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            deleteCommand.Parameters.AddWithValue("invoice_id", documentId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in draft.Lines.OrderBy(static line => line.LineNumber))
        {
            await using var insertLineCommand = connection.CreateCommand();
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                """
                insert into invoice_lines (
                  id,
                  company_id,
                  invoice_id,
                  line_number,
                  revenue_account_id,
                  description,
                  quantity,
                  unit_price,
                  line_amount,
                  tax_code_id,
                  tax_amount,
                  item_id,
                  warehouse_id,
                  uom_code,
                  created_at,
                  updated_at
                )
                values (
                  @id,
                  @company_id,
                  @invoice_id,
                  @line_number,
                  @revenue_account_id,
                  @description,
                  @quantity,
                  @unit_price,
                  @line_amount,
                  @tax_code_id,
                  @tax_amount,
                  @item_id,
                  @warehouse_id,
                  @uom_code,
                  now(),
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertLineCommand.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
            insertLineCommand.Parameters.AddWithValue("invoice_id", documentId);
            insertLineCommand.Parameters.AddWithValue("line_number", line.LineNumber);
            insertLineCommand.Parameters.AddWithValue("revenue_account_id", line.RevenueAccountId);
            insertLineCommand.Parameters.AddWithValue("description", line.Description.Trim());
            insertLineCommand.Parameters.AddWithValue("quantity", Round6(line.Quantity));
            insertLineCommand.Parameters.AddWithValue("unit_price", Round6(line.UnitPrice));
            insertLineCommand.Parameters.AddWithValue("line_amount", Round6(line.Quantity * line.UnitPrice));
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("tax_code_id", NpgsqlDbType.Uuid) { TypedValue = line.TaxCodeId });
            insertLineCommand.Parameters.AddWithValue("tax_amount", Round6(line.TaxAmount));
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("item_id", NpgsqlDbType.Uuid) { TypedValue = line.ItemId });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("warehouse_id", NpgsqlDbType.Uuid) { TypedValue = line.WarehouseId });
            insertLineCommand.Parameters.AddWithValue("uom_code", string.IsNullOrWhiteSpace(line.UomCode) ? (object)DBNull.Value : line.UomCode.Trim().ToUpperInvariant());
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

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
            update invoices
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
            throw new InvalidOperationException("Only draft invoices can be submitted.");
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, "submitted");
    }

    private static void ValidateDraft(InvoiceDraftSaveModel draft)
    {
        if (draft.CustomerId == Guid.Empty)
        {
            throw new InvalidOperationException("Invoice draft requires a customer.");
        }

        if (draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Invoice draft must contain at least one line.");
        }

        foreach (var line in draft.Lines)
        {
            if (line.LineNumber <= 0 || line.RevenueAccountId == Guid.Empty || string.IsNullOrWhiteSpace(line.Description))
            {
                throw new InvalidOperationException("Invoice draft lines must have a line number, revenue account, and description.");
            }

            if (line.Quantity <= 0m || line.UnitPrice < 0m || line.TaxAmount < 0m)
            {
                throw new InvalidOperationException("Invoice draft quantities and amounts must be non-negative, with quantity greater than zero.");
            }

            if (line.TaxAmount > 0m && line.TaxCodeId is null)
            {
                throw new InvalidOperationException("Invoice draft lines with tax must provide a tax code.");
            }

            var hasOutboundInventoryField =
                line.ItemId.HasValue ||
                line.WarehouseId.HasValue ||
                !string.IsNullOrWhiteSpace(line.UomCode);

            if (!hasOutboundInventoryField)
            {
                continue;
            }

            if (!line.ItemId.HasValue || !line.WarehouseId.HasValue || string.IsNullOrWhiteSpace(line.UomCode))
            {
                throw new InvalidOperationException("Invoice outbound inventory hand-off must include item, warehouse, and UOM together once the line joins shipment-first bridging.");
            }
        }

        ValidateFx(draft.TransactionCurrencyCode, draft.BaseCurrencyCode, draft.FxRate);
    }

    private static void BindHeader(
        NpgsqlCommand command,
        InvoiceDraftSaveModel draft,
        Guid documentId,
        string entityNumber,
        string displayNumber,
        bool includeIdentity = true)
    {
        var (fxRate, fxSource, fxRequestedDate, fxEffectiveDate) = ResolveFx(draft.TransactionCurrencyCode, draft.BaseCurrencyCode, draft.InvoiceDate, draft.FxRate, draft.FxEffectiveDate, draft.FxSource);
        var subtotalAmount = Round6(draft.Lines.Sum(static line => line.Quantity * line.UnitPrice));
        var taxAmount = Round6(draft.Lines.Sum(static line => line.TaxAmount));
        var totalAmount = Round6(subtotalAmount + taxAmount);

        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("company_id", draft.CompanyId.Value);
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("entity_number", entityNumber);
            command.Parameters.AddWithValue("invoice_number", displayNumber);
            command.Parameters.AddWithValue("created_by_user_id", draft.UserId.Value);
        }

        command.Parameters.AddWithValue("customer_id", draft.CustomerId);
        command.Parameters.AddWithValue("invoice_date", draft.InvoiceDate);
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
        command.Parameters.AddWithValue("customer_po_number", string.IsNullOrWhiteSpace(draft.CustomerPoNumber) ? (object)DBNull.Value : draft.CustomerPoNumber.Trim());
        command.Parameters.Add(new NpgsqlParameter<Guid?>("sales_order_id", NpgsqlDbType.Uuid) { TypedValue = draft.SalesOrderId });
    }

    private static void ValidateFx(string transactionCurrencyCode, string baseCurrencyCode, decimal? fxRate)
    {
        if (!string.Equals(transactionCurrencyCode.Trim(), baseCurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (!fxRate.HasValue || fxRate.Value <= 0m))
        {
            throw new InvalidOperationException("Foreign-currency invoice drafts must provide a positive FX rate.");
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

    // EnsureInventoryGradeInvoiceLineColumnsAsync used to live here.
    // The three columns (item_id, warehouse_id, uom_code) are now in
    // deploy/migrations/2026-05-08-invoice-line-inventory-columns.sql,
    // applied once at deploy time by the migration runner. The inline
    // helper + its information_schema short-circuit are gone — the
    // 30 s self-deadlock that prompted commit 2ef2640 stays fixed
    // because the read path no longer takes any schema-level lock.

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
            select entity_number, invoice_number
            from invoices
            where company_id = @company_id
              and id = @document_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The invoice draft could not be found in the active company context.");
        }

        return (
            reader.GetString(reader.GetOrdinal("entity_number")),
            reader.GetString(reader.GetOrdinal("invoice_number")));
    }

    /// <summary>
    /// Optimistic-concurrency fallback probe. SaveDraftAsync's UPDATE
    /// returns 0 affected rows for several reasons (wrong company,
    /// already posted, expected_updated_at mismatch). When the caller
    /// supplied an expected timestamp, this query distinguishes the
    /// "still a draft, just stale snapshot" case so the route handler
    /// can surface 409 instead of the generic 400.
    /// </summary>
    private static async Task<bool> DraftStillExistsAsync(
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
            select 1
            from invoices
            where id = @document_id
              and company_id = @company_id
              and status = 'draft'
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }
}
