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

        // Schema for item_id / warehouse_id / uom_code / task_line_id on invoice_lines
        // is applied via deploy/migrations/2026-05-08-invoice-line-
        // inventory-columns.sql and 2026-06-01-invoice-line-task-line-link.sql.
        // No inline ALTER on the read path.

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
                            l.id,
                            l.line_number,
                            l.revenue_account_id,
                            l.description,
                            l.quantity,
                           l.unit_price,
                           l.line_amount,
                           l.tax_amount,
                            l.tax_code_id,
                           case
                             when l.tax_amount > 0 then coalesce(
                               (
                                 select stm.payable_account_id
                                   from sales_tax_code_components tcc
                              left join sales_tax_account_mappings stm
                                     on stm.company_id = tcc.company_id
                                    and stm.tax_component_id = tcc.tax_component_id
                                    and stm.applies_to in ('sales', 'both')
                                    and stm.payable_account_id is not null
                                  where tcc.company_id = l.company_id::text
                                    and tcc.tax_code_id = l.tax_code_id
                                    and coalesce(tcc.applies_to, tc.applies_to, 'both') in ('sales', 'both')
                               order by tcc.sequence, stm.updated_at desc, stm.created_at desc
                                  limit 1
                               ),
                               tc.payable_account_id,
                               (
                                 select a.id
                                   from accounts a
                                  where a.company_id = l.company_id
                                    and a.root_type = 'liability'
                                    and a.detail_type = 'tax'
                                    and a.is_active = true
                               order by
                                    case
                                      when a.system_key = 'tax:payable' then 0
                                      when a.system_role = 'tax_payable' then 1
                                      when a.code = '25000' then 2
                                      when a.name ilike '%Sales Tax Payable%' then 3
                                      else 4
                                    end,
                                    a.code
                                  limit 1
                               ))
                             else tc.payable_account_id
                           end as payable_account_id,
                            l.item_id,
                            l.warehouse_id,
                            l.uom_code,
                            l.task_id,
                            l.task_line_id
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
                    reader.IsDBNull(reader.GetOrdinal("uom_code")) ? null : reader.GetString(reader.GetOrdinal("uom_code")),
                    reader.IsDBNull(reader.GetOrdinal("task_id")) ? null : reader.GetGuid(reader.GetOrdinal("task_id")),
                    reader.IsDBNull(reader.GetOrdinal("task_line_id")) ? null : reader.GetGuid(reader.GetOrdinal("task_line_id")),
                    reader.GetGuid(reader.GetOrdinal("id"))));
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

        // Inventory-grade and task-line provenance invoice_lines columns are managed by the
        // migration runner (see 2026-05-08-invoice-line-inventory-
        // columns.sql and 2026-06-01-invoice-line-task-line-link.sql);
        // no inline ALTER on this write path.

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
                  task_id,
                  task_line_id,
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
                  @task_id,
                  @task_line_id,
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
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("task_id", NpgsqlDbType.Uuid) { TypedValue = line.TaskId });
            insertLineCommand.Parameters.Add(new NpgsqlParameter<Guid?>("task_line_id", NpgsqlDbType.Uuid) { TypedValue = line.TaskLineId });
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SourceDocumentDraftSaveResult(documentId, entityNumber, displayNumber, "draft");
    }

    public async Task<IReadOnlyList<InvoiceLineTaskLink>> ListLinkedTaskLineMappingsAsync(
        CompanyId companyId,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, task_id, task_line_id
            from invoice_lines
            where company_id = @company_id
              and invoice_id = @invoice_id
              and task_id is not null
            order by line_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("invoice_id", invoiceId);

        var rows = new List<InvoiceLineTaskLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new InvoiceLineTaskLink(
                InvoiceLineId: reader.GetGuid(0),
                TaskId: reader.GetGuid(1),
                TaskLineId: reader.IsDBNull(2) ? null : reader.GetGuid(2)));
        }

        return rows;
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

    public async Task<InvoiceDocument?> VoidAsync(
        CompanyId companyId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var invoice = await LoadInvoiceForVoidAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        if (invoice is null)
        {
            return null;
        }

        if (string.Equals(invoice.Status, "draft", StringComparison.Ordinal))
        {
            await MarkInvoiceVoidedAsync(
                connection,
                transaction,
                companyId,
                documentId,
                expectedStatus: "draft",
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return await GetForPostingAsync(companyId, documentId, cancellationToken);
        }

        if (!string.Equals(invoice.Status, "posted", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invoice in status '{invoice.Status}' cannot be voided.");
        }

        await EnsureInvoiceHasNoSettlementApplicationsAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        var journal = await ReadPostedInvoiceJournalAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        var originalLines = await ReadPostedJournalLinesForReversalAsync(
            connection,
            transaction,
            companyId,
            journal.Id,
            cancellationToken);

        if (originalLines.Count == 0)
        {
            throw new InvalidOperationException("Posted invoice journal entry has no lines to reverse.");
        }

        await InsertVoidedInvoiceJournalAsync(
            connection,
            transaction,
            invoice,
            journal,
            originalLines,
            cancellationToken);

        await VoidInvoiceOpenItemAsync(
            connection,
            transaction,
            companyId,
            documentId,
            cancellationToken);

        await MarkInvoiceVoidedAsync(
            connection,
            transaction,
            companyId,
            documentId,
            expectedStatus: "posted",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetForPostingAsync(companyId, documentId, cancellationToken);
    }

    private static async Task<InvoiceVoidHeaderSnapshot?> LoadInvoiceForVoidAsync(
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
            select id,
                   company_id,
                   status,
                   invoice_date,
                   created_by_user_id
            from invoices
            where company_id = @company_id
              and id = @document_id
            for update;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvoiceVoidHeaderSnapshot(
            reader.GetGuid(reader.GetOrdinal("id")),
            companyId,
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("invoice_date")),
            reader.GetString(reader.GetOrdinal("created_by_user_id")));
    }

    private static async Task MarkInvoiceVoidedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid documentId,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update invoices
            set status = 'voided',
                updated_at = now()
            where company_id = @company_id
              and id = @document_id
              and status = @expected_status;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("expected_status", expectedStatus);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException("Invoice could not be marked voided.");
        }
    }

    private static async Task EnsureInvoiceHasNoSettlementApplicationsAsync(
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
            select count(*)
            from settlement_applications sa
            inner join ar_open_items oi
              on oi.company_id = sa.company_id
             and oi.id = sa.target_open_item_id
            where sa.company_id = @company_id
              and sa.target_open_item_type = 'ar_open_item'
              and oi.source_type = 'invoice'
              and oi.source_id = @document_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        var applicationCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (applicationCount > 0)
        {
            throw new InvalidOperationException(
                "This invoice already has payment/application history. Void or reverse the related Receive Payment or credit application before voiding the invoice.");
        }
    }

    private static async Task<InvoiceJournalHeaderSnapshot> ReadPostedInvoiceJournalAsync(
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
            select id,
                   transaction_currency_code,
                   base_currency_code,
                   exchange_rate,
                   exchange_rate_date,
                   exchange_rate_source,
                   created_by_user_id
            from journal_entries
            where company_id = @company_id
              and source_type = 'invoice'
              and source_id = @document_id
              and status = 'posted'
            order by posted_at desc nulls last, created_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Posted invoice has no linked journal entry to reverse.");
        }

        return new InvoiceJournalHeaderSnapshot(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("transaction_currency_code")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetDecimal(reader.GetOrdinal("exchange_rate")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("exchange_rate_date")),
            reader.GetString(reader.GetOrdinal("exchange_rate_source")),
            reader.GetString(reader.GetOrdinal("created_by_user_id")));
    }

    private static async Task<IReadOnlyList<InvoiceJournalLineSnapshot>> ReadPostedJournalLinesForReversalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            select account_id,
                   description,
                   party_type,
                   party_id,
                   tx_debit,
                   tx_credit,
                   debit,
                   credit,
                   posting_role,
                   source_line_number
            from journal_entry_lines
            where company_id = @company_id
              and journal_entry_id = @journal_entry_id
            order by line_number;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);

        var lines = new List<InvoiceJournalLineSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new InvoiceJournalLineSnapshot(
                reader.GetGuid(reader.GetOrdinal("account_id")),
                reader.GetString(reader.GetOrdinal("description")),
                reader.IsDBNull(reader.GetOrdinal("party_type")) ? null : reader.GetString(reader.GetOrdinal("party_type")),
                reader.IsDBNull(reader.GetOrdinal("party_id")) ? null : reader.GetGuid(reader.GetOrdinal("party_id")),
                reader.GetDecimal(reader.GetOrdinal("tx_debit")),
                reader.GetDecimal(reader.GetOrdinal("tx_credit")),
                reader.GetDecimal(reader.GetOrdinal("debit")),
                reader.GetDecimal(reader.GetOrdinal("credit")),
                reader.GetString(reader.GetOrdinal("posting_role")),
                reader.IsDBNull(reader.GetOrdinal("source_line_number")) ? null : reader.GetInt32(reader.GetOrdinal("source_line_number"))));
        }

        return lines;
    }

    private static async Task InsertVoidedInvoiceJournalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InvoiceVoidHeaderSnapshot invoice,
        InvoiceJournalHeaderSnapshot journal,
        IReadOnlyList<InvoiceJournalLineSnapshot> originalLines,
        CancellationToken cancellationToken)
    {
        var voidJournalEntryId = Guid.NewGuid();
        var postedAt = DateTimeOffset.UtcNow;
        var journalDisplayNumber = await ReserveJournalDisplayNumberAsync(
            connection,
            transaction,
            invoice.CompanyId,
            cancellationToken);
        var entityNumber = await ReserveEntityNumberAsync(
            connection,
            transaction,
            invoice.CompanyId,
            invoice.InvoiceDate.Year,
            cancellationToken);
        var idempotencyKey = $"invoice-void:{invoice.Id:D}";

        await using (var insertEntryCommand = connection.CreateCommand())
        {
            insertEntryCommand.Transaction = transaction;
            insertEntryCommand.CommandText =
                """
                insert into journal_entries (
                  id, company_id, entity_number, display_number, status,
                  source_type, source_id,
                  transaction_currency_code, base_currency_code,
                  exchange_rate, exchange_rate_date, exchange_rate_source,
                  fx_rate_snapshot_id,
                  total_tx_debit, total_tx_credit, total_debit, total_credit,
                  posting_run_id, idempotency_key, posted_at, created_by_user_id, created_at
                )
                values (
                  @id, @company_id, @entity_number, @display_number, 'posted',
                  'invoice_void', @source_id,
                  @transaction_currency_code, @base_currency_code,
                  @exchange_rate, @exchange_rate_date, @exchange_rate_source,
                  null,
                  @total_tx_debit, @total_tx_credit, @total_debit, @total_credit,
                  @posting_run_id, @idempotency_key, @posted_at, @created_by_user_id, now()
                );
                """;
            insertEntryCommand.Parameters.AddWithValue("id", voidJournalEntryId);
            insertEntryCommand.Parameters.AddWithValue("company_id", invoice.CompanyId.Value);
            insertEntryCommand.Parameters.AddWithValue("entity_number", entityNumber);
            insertEntryCommand.Parameters.AddWithValue("display_number", journalDisplayNumber);
            insertEntryCommand.Parameters.AddWithValue("source_id", invoice.Id);
            insertEntryCommand.Parameters.AddWithValue("transaction_currency_code", journal.TransactionCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("base_currency_code", journal.BaseCurrencyCode);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate", RoundRate(journal.ExchangeRate));
            insertEntryCommand.Parameters.Add("exchange_rate_date", NpgsqlDbType.Date).Value =
                journal.ExchangeRateDate.ToDateTime(TimeOnly.MinValue);
            insertEntryCommand.Parameters.AddWithValue("exchange_rate_source", journal.ExchangeRateSource);
            insertEntryCommand.Parameters.AddWithValue("total_tx_debit", RoundTx(originalLines.Sum(static line => line.TxCredit)));
            insertEntryCommand.Parameters.AddWithValue("total_tx_credit", RoundTx(originalLines.Sum(static line => line.TxDebit)));
            insertEntryCommand.Parameters.AddWithValue("total_debit", RoundBase(originalLines.Sum(static line => line.Credit)));
            insertEntryCommand.Parameters.AddWithValue("total_credit", RoundBase(originalLines.Sum(static line => line.Debit)));
            insertEntryCommand.Parameters.AddWithValue("posting_run_id", Guid.NewGuid());
            insertEntryCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insertEntryCommand.Parameters.AddWithValue("posted_at", postedAt);
            insertEntryCommand.Parameters.AddWithValue("created_by_user_id", journal.CreatedByUserId);
            await insertEntryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var lineNumber = 1;
        foreach (var originalLine in originalLines)
        {
            await InsertJournalAndLedgerLineAsync(
                connection,
                transaction,
                invoice.CompanyId,
                voidJournalEntryId,
                lineNumber++,
                originalLine.AccountId,
                originalLine.PartyType,
                originalLine.PartyId,
                $"Void {originalLine.Description}",
                journal.TransactionCurrencyCode,
                txDebit: originalLine.TxCredit,
                txCredit: originalLine.TxDebit,
                debit: originalLine.Credit,
                credit: originalLine.Debit,
                postingDate: invoice.InvoiceDate,
                postingRole: $"void:{originalLine.PostingRole}",
                sourceLineNumber: originalLine.SourceLineNumber,
                cancellationToken);
        }
    }

    private static async Task VoidInvoiceOpenItemAsync(
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
            update ar_open_items
            set status = 'voided',
                open_amount_tx = 0,
                open_amount_base = 0,
                updated_at = now()
            where company_id = @company_id
              and source_type = 'invoice'
              and source_id = @document_id
              and status <> 'voided';
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertJournalAndLedgerLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid journalEntryId,
        int lineNumber,
        Guid accountId,
        string? partyType,
        Guid? partyId,
        string description,
        string transactionCurrencyCode,
        decimal txDebit,
        decimal txCredit,
        decimal debit,
        decimal credit,
        DateOnly postingDate,
        string postingRole,
        int? sourceLineNumber,
        CancellationToken cancellationToken)
    {
        var journalEntryLineId = Guid.NewGuid();

        await using (var insertLineCommand = connection.CreateCommand())
        {
            insertLineCommand.Transaction = transaction;
            insertLineCommand.CommandText =
                """
                insert into journal_entry_lines (
                  id, company_id, journal_entry_id, line_number,
                  account_id, description, party_type, party_id,
                  tx_debit, tx_credit, debit, credit,
                  tax_component_type, control_role, posting_role, source_line_number,
                  created_at
                )
                values (
                  @id, @company_id, @journal_entry_id, @line_number,
                  @account_id, @description, @party_type, @party_id,
                  @tx_debit, @tx_credit, @debit, @credit,
                  null, null, @posting_role, @source_line_number,
                  now()
                );
                """;
            insertLineCommand.Parameters.AddWithValue("id", journalEntryLineId);
            insertLineCommand.Parameters.AddWithValue("company_id", companyId.Value);
            insertLineCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
            insertLineCommand.Parameters.AddWithValue("line_number", lineNumber);
            insertLineCommand.Parameters.AddWithValue("account_id", accountId);
            insertLineCommand.Parameters.AddWithValue("description", description);
            insertLineCommand.Parameters.AddWithValue("party_type", string.IsNullOrWhiteSpace(partyType) ? DBNull.Value : partyType);
            insertLineCommand.Parameters.AddWithValue("party_id", (object?)partyId ?? DBNull.Value);
            insertLineCommand.Parameters.AddWithValue("tx_debit", RoundTx(txDebit));
            insertLineCommand.Parameters.AddWithValue("tx_credit", RoundTx(txCredit));
            insertLineCommand.Parameters.AddWithValue("debit", RoundBase(debit));
            insertLineCommand.Parameters.AddWithValue("credit", RoundBase(credit));
            insertLineCommand.Parameters.AddWithValue("posting_role", postingRole);
            insertLineCommand.Parameters.AddWithValue("source_line_number", (object?)sourceLineNumber ?? DBNull.Value);
            await insertLineCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insertLedgerCommand = connection.CreateCommand();
        insertLedgerCommand.Transaction = transaction;
        insertLedgerCommand.CommandText =
            """
            insert into ledger_entries (
              id, company_id, journal_entry_id, journal_entry_line_id,
              posting_date, account_id, debit, credit,
              transaction_currency_code, tx_debit, tx_credit,
              created_at
            )
            values (
              @id, @company_id, @journal_entry_id, @journal_entry_line_id,
              @posting_date, @account_id, @debit, @credit,
              @transaction_currency_code, @tx_debit, @tx_credit,
              now()
            );
            """;
        insertLedgerCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        insertLedgerCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertLedgerCommand.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        insertLedgerCommand.Parameters.AddWithValue("journal_entry_line_id", journalEntryLineId);
        insertLedgerCommand.Parameters.AddWithValue("posting_date", postingDate);
        insertLedgerCommand.Parameters.AddWithValue("account_id", accountId);
        insertLedgerCommand.Parameters.AddWithValue("debit", RoundBase(debit));
        insertLedgerCommand.Parameters.AddWithValue("credit", RoundBase(credit));
        insertLedgerCommand.Parameters.AddWithValue("transaction_currency_code", transactionCurrencyCode);
        insertLedgerCommand.Parameters.AddWithValue("tx_debit", RoundTx(txDebit));
        insertLedgerCommand.Parameters.AddWithValue("tx_credit", RoundTx(txCredit));
        await insertLedgerCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReserveJournalDisplayNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var seedNumber = await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
            connection,
            transaction,
            companyId,
            "journal_entries",
            "display_number",
            "^JE-[0-9]+$",
            4,
            cancellationToken);

        return await PostgresSourceDocumentDraftNumbering.ReserveAsync(
            connection,
            transaction,
            companyId,
            "journal-entry-display",
            "JE-",
            6,
            seedNumber,
            cancellationToken);
    }

    private static async Task<string> ReserveEntityNumberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        var seedNumber = await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
            connection,
            transaction,
            companyId,
            year,
            cancellationToken);

        return await PostgresSourceDocumentDraftNumbering.ReserveAsync(
            connection,
            transaction,
            companyId,
            $"entity-number:all:{year}",
            $"EN{year}",
            5,
            seedNumber,
            cancellationToken);
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

            // Product/service identity alone is valid for ordinary AR lines.
            // The shipment-first bridge starts only when the line carries
            // inventory anchors beyond ItemId: warehouse and UOM.
            var hasOutboundInventoryField =
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

    private static decimal RoundTx(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);

    private static decimal RoundBase(decimal value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    private static decimal RoundRate(decimal value) =>
        Math.Round(value, 10, MidpointRounding.ToEven);

    private sealed record InvoiceVoidHeaderSnapshot(
        Guid Id,
        CompanyId CompanyId,
        string Status,
        DateOnly InvoiceDate,
        string CreatedByUserId);

    private sealed record InvoiceJournalHeaderSnapshot(
        Guid Id,
        string TransactionCurrencyCode,
        string BaseCurrencyCode,
        decimal ExchangeRate,
        DateOnly ExchangeRateDate,
        string ExchangeRateSource,
        string CreatedByUserId);

    private sealed record InvoiceJournalLineSnapshot(
        Guid AccountId,
        string Description,
        string? PartyType,
        Guid? PartyId,
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit,
        string PostingRole,
        int? SourceLineNumber);

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
