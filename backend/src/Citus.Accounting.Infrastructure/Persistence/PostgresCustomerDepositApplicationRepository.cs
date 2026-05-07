using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M5 iter 4: persists customer-deposit → invoice applications + builds
/// the posting document. Pro-rata logic:
///
///   share = MIN(invoice.amount_base / so.total_amount_base, 1.0)
///   desired = share × SUM(deposits.original_amount_base)
///   cap = MIN(desired, SUM(deposits.open_amount_base), invoice.open_amount_base)
///
/// The cap is then walked across deposits FIFO (oldest created_at first)
/// and each slice is persisted: settlement_applications row,
/// ar_open_items updates on both source (deposit) and target (invoice),
/// customer_deposits.status flip to 'closed' / 'partially_applied'.
///
/// V1 same-currency assumption (carried from M5 iter 3): SO currency =
/// base currency = deposit currency. Multi-currency FX revaluation lands
/// in a follow-up iter.
/// </summary>
public sealed class PostgresCustomerDepositApplicationRepository : ICustomerDepositApplicationRepository
{
    private const string SettlementApplicationType = "customer_deposit_application";
    private const string SettlementSourceType = "customer_deposit";
    private const string SettlementTargetType = "ar_open_items";

    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresCustomerDepositApplicationRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<CustomerDepositApplicationPreparation> PrepareApplicationAsync(
        CompanyId companyId,
        UserId userId,
        Guid invoiceDocumentId,
        CancellationToken cancellationToken)
    {
        if (invoiceDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Invoice document id is required.", nameof(invoiceDocumentId));
        }

        var emptyResult = new CustomerDepositApplicationPreparation(
            Document: null,
            Applications: Array.Empty<CustomerDepositApplicationOutcome>());

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Look up the invoice header (so + amount), the invoice's AR open
        // item, the resolved AR + customer deposit account ids, and the
        // company's base currency in one round-trip. Returns null when the
        // invoice has no source SO (legacy direct-create path).
        InvoiceContext context;
        await using (var ctxCommand = connection.CreateCommand())
        {
            ctxCommand.Transaction = transaction;
            ctxCommand.CommandText = """
                select
                  i.sales_order_id,
                  i.customer_id,
                  i.total_amount,
                  i.fx_rate,
                  i.base_currency_code,
                  oi.id              as invoice_open_item_id,
                  oi.open_amount_base as invoice_open_amount_base,
                  so.total_amount    as so_total_amount,
                  so.fx_rate         as so_fx_rate,
                  ar.id              as ar_account_id,
                  dep.id             as customer_deposit_account_id
                from invoices i
                left join sales_orders so on so.company_id = i.company_id and so.id = i.sales_order_id
                left join ar_open_items oi
                  on oi.company_id = i.company_id
                 and oi.source_type = 'invoice'
                 and oi.source_id = i.id
                left join accounts ar
                  on ar.company_id = i.company_id
                 and ar.system_role = 'accounts_receivable'
                 and ar.is_active = true
                left join accounts dep
                  on dep.company_id = i.company_id
                 and dep.system_role = 'customer_deposit'
                 and dep.is_active = true
                where i.company_id = @company_id and i.id = @invoice_id
                limit 1;
                """;
            ctxCommand.Parameters.AddWithValue("company_id", companyId.Value);
            ctxCommand.Parameters.AddWithValue("invoice_id", invoiceDocumentId);
            await using var reader = await ctxCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return emptyResult;
            }

            context = new InvoiceContext(
                SalesOrderId: reader.IsDBNull(0) ? null : reader.GetGuid(0),
                CustomerId: reader.GetGuid(1),
                InvoiceTotalTx: reader.GetDecimal(2),
                InvoiceFxRate: reader.GetDecimal(3),
                BaseCurrency: reader.GetString(4),
                InvoiceOpenItemId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
                InvoiceOpenAmountBase: reader.IsDBNull(6) ? 0m : reader.GetDecimal(6),
                SoTotalTx: reader.IsDBNull(7) ? 0m : reader.GetDecimal(7),
                SoFxRate: reader.IsDBNull(8) ? 1m : reader.GetDecimal(8),
                ArAccountId: reader.IsDBNull(9) ? Guid.Empty : reader.GetGuid(9),
                CustomerDepositAccountId: reader.IsDBNull(10) ? Guid.Empty : reader.GetGuid(10));
        }

        // No SO link → nothing to clear (legacy direct-create invoice).
        if (context.SalesOrderId is null)
        {
            return emptyResult;
        }

        if (context.InvoiceOpenItemId is null || context.InvoiceOpenAmountBase <= 0m)
        {
            // Invoice's AR row is missing or already fully settled — nothing
            // to apply against.
            return emptyResult;
        }

        // Compute base-currency totals. V1 same-currency: fx_rate = 1.
        var invoiceTotalBase = Math.Round(context.InvoiceTotalTx * context.InvoiceFxRate, 6, MidpointRounding.ToEven);
        var soTotalBase = Math.Round(context.SoTotalTx * context.SoFxRate, 6, MidpointRounding.ToEven);
        if (soTotalBase <= 0m)
        {
            return emptyResult;
        }

        // Look up open deposits for this SO (FIFO by created_at).
        var deposits = new List<DepositRow>();
        await using (var depositsCommand = connection.CreateCommand())
        {
            depositsCommand.Transaction = transaction;
            depositsCommand.CommandText = """
                select
                  cd.id              as deposit_id,
                  cd.display_number,
                  cd.original_amount_base,
                  cd.status,
                  oi.id              as deposit_open_item_id,
                  oi.open_amount_base as deposit_open_amount_base
                from customer_deposits cd
                join ar_open_items oi
                  on oi.company_id = cd.company_id
                 and oi.source_type = 'customer_deposit'
                 and oi.source_id = cd.id
                where cd.company_id = @company_id
                  and cd.source_sales_order_id = @so_id
                  and cd.status in ('open', 'partially_applied')
                  and oi.status in ('open', 'partially_applied')
                  and oi.open_amount_base > 0
                order by cd.created_at asc;
                """;
            depositsCommand.Parameters.AddWithValue("company_id", companyId.Value);
            depositsCommand.Parameters.AddWithValue("so_id", context.SalesOrderId.Value);
            await using var reader = await depositsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                deposits.Add(new DepositRow(
                    DepositId: reader.GetGuid(0),
                    DisplayNumber: reader.GetString(1),
                    OriginalAmountBase: reader.GetDecimal(2),
                    Status: reader.GetString(3),
                    OpenItemId: reader.GetGuid(4),
                    OpenAmountBase: reader.GetDecimal(5)));
            }
        }

        if (deposits.Count == 0)
        {
            return emptyResult;
        }

        if (context.ArAccountId == Guid.Empty || context.CustomerDepositAccountId == Guid.Empty)
        {
            // Missing CoA seeding — fail loud upstream rather than dropping
            // applications silently.
            throw new InvalidOperationException(
                "Customer-deposit application requires both AR (system_role='accounts_receivable') and " +
                "Customer Deposit (system_role='customer_deposit') accounts in the Chart of Accounts.");
        }

        // Pro-rata cap.
        var totalOriginalBase = deposits.Sum(d => d.OriginalAmountBase);
        var totalOpenBase = deposits.Sum(d => d.OpenAmountBase);
        var share = Math.Min(invoiceTotalBase / soTotalBase, 1m);
        var desired = Math.Round(share * totalOriginalBase, 2, MidpointRounding.ToEven);
        var capRemaining = new[] { desired, totalOpenBase, context.InvoiceOpenAmountBase }.Min();
        capRemaining = Math.Round(capRemaining, 2, MidpointRounding.ToEven);
        if (capRemaining <= 0m)
        {
            return emptyResult;
        }

        // Walk deposits FIFO.
        var applicationLines = new List<CustomerDepositApplicationDocumentLine>();
        var outcomes = new List<CustomerDepositApplicationOutcome>();
        var remainingInvoiceOpen = context.InvoiceOpenAmountBase;
        var lineNumber = 0;
        foreach (var deposit in deposits)
        {
            if (capRemaining <= 0m) break;
            var applyThis = Math.Min(deposit.OpenAmountBase, capRemaining);
            applyThis = Math.Round(applyThis, 2, MidpointRounding.ToEven);
            if (applyThis <= 0m) continue;

            await ApplyDepositSliceAsync(
                connection,
                transaction,
                companyId,
                userId,
                deposit,
                context.InvoiceOpenItemId.Value,
                remainingInvoiceOpen,
                applyThis,
                cancellationToken);

            lineNumber++;
            applicationLines.Add(new CustomerDepositApplicationDocumentLine(
                lineNumber: lineNumber,
                sourceCustomerDepositId: deposit.DepositId,
                sourceCustomerDepositArOpenItemId: deposit.OpenItemId,
                targetInvoiceArOpenItemId: context.InvoiceOpenItemId.Value,
                description: $"Apply deposit {deposit.DisplayNumber}",
                appliedAmountBase: applyThis));

            outcomes.Add(new CustomerDepositApplicationOutcome(
                CustomerDepositId: deposit.DepositId,
                CustomerDepositDisplayNumber: deposit.DisplayNumber,
                AppliedAmountBase: applyThis,
                DepositFullyClosed: deposit.OpenAmountBase - applyThis <= 0m));

            remainingInvoiceOpen = Math.Round(remainingInvoiceOpen - applyThis, 6, MidpointRounding.ToEven);
            capRemaining = Math.Round(capRemaining - applyThis, 6, MidpointRounding.ToEven);
        }

        if (applicationLines.Count == 0)
        {
            return emptyResult;
        }

        // Reserve entity + display numbers from the shared sequence tables
        // before committing — keeps the per-second collision risk of a
        // timestamp-based scheme out of the picture (two near-simultaneous
        // invoice posts each triggered an application doc with an identical
        // DEP-APPL-yyMMddHHmmss). Sequence advances inside the transaction
        // are correctly serialized by the row update on company_numbering_sequences.
        var documentDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var year = documentDate.Year;
        var entityNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
            connection,
            transaction,
            companyId,
            $"entity-number:all:{year}",
            $"EN{year}",
            5,
            await PostgresSourceDocumentDraftNumbering.FindEntitySeedNumberAsync(
                connection, transaction, companyId, year, cancellationToken),
            cancellationToken);

        // No dedicated parent table for customer-deposit-application docs
        // (settlement_applications is shared across application types), so
        // there is nothing to scan for an existing seed — bootstrap from 1
        // and let the company_numbering_sequences row carry the cursor.
        var displayNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
            connection,
            transaction,
            companyId,
            "customer-deposit-application-display",
            "DAPL-",
            6,
            1L,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // Build the posting document. The application is its own JE source
        // (separate from the invoice JE) so it shows up cleanly in the GL
        // and stays journal-layer idempotent on its own id.
        var document = new CustomerDepositApplicationDocument(
            id: Guid.NewGuid(),
            companyId: companyId,
            entityNumber: EntityNumber.Parse(entityNumber),
            displayNumber: new DocumentNumber(displayNumber),
            documentDate: documentDate,
            customerId: context.CustomerId,
            receivableAccountId: context.ArAccountId,
            customerDepositAccountId: context.CustomerDepositAccountId,
            baseCurrencyCode: new CurrencyCode(context.BaseCurrency),
            lines: applicationLines,
            invoiceDocumentId: invoiceDocumentId);

        return new CustomerDepositApplicationPreparation(document, outcomes);
    }

    private static async Task ApplyDepositSliceAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        CompanyId companyId,
        UserId userId,
        DepositRow deposit,
        Guid invoiceOpenItemId,
        decimal invoiceOpenRemaining,
        decimal applyAmount,
        CancellationToken cancellationToken)
    {
        // settlement_applications row links the deposit's open item (source)
        // to the invoice's open item (target).
        await using (var insertApp = connection.CreateCommand())
        {
            insertApp.Transaction = transaction;
            insertApp.CommandText = """
                insert into settlement_applications (
                  id, company_id, application_type, source_type, source_id,
                  target_open_item_type, target_open_item_id,
                  applied_amount_tx, applied_amount_base,
                  settlement_fx_rate, realized_fx_amount,
                  created_at, created_by_user_id)
                values (
                  gen_random_uuid(), @company_id, @application_type, @source_type, @source_id,
                  @target_open_item_type, @target_open_item_id,
                  @applied_amount_tx, @applied_amount_base,
                  null, null,
                  now(), @user_id);
                """;
            insertApp.Parameters.AddWithValue("company_id", companyId.Value);
            insertApp.Parameters.AddWithValue("application_type", SettlementApplicationType);
            insertApp.Parameters.AddWithValue("source_type", SettlementSourceType);
            insertApp.Parameters.AddWithValue("source_id", deposit.DepositId);
            insertApp.Parameters.AddWithValue("target_open_item_type", SettlementTargetType);
            insertApp.Parameters.AddWithValue("target_open_item_id", invoiceOpenItemId);
            insertApp.Parameters.AddWithValue("applied_amount_tx", applyAmount);
            insertApp.Parameters.AddWithValue("applied_amount_base", applyAmount);
            insertApp.Parameters.AddWithValue("user_id", userId.Value);
            await insertApp.ExecuteNonQueryAsync(cancellationToken);
        }

        // Source AR open item (the deposit's credit row): reduce open amount,
        // flip status if fully consumed.
        var depositRemaining = Math.Round(deposit.OpenAmountBase - applyAmount, 6, MidpointRounding.ToEven);
        var depositStatus = depositRemaining <= 0m ? "closed" : "partially_applied";
        await using (var updateDepositOi = connection.CreateCommand())
        {
            updateDepositOi.Transaction = transaction;
            updateDepositOi.CommandText = """
                update ar_open_items
                  set open_amount_tx = @open_amount,
                      open_amount_base = @open_amount,
                      status = @status,
                      updated_at = now()
                where company_id = @company_id and id = @id;
                """;
            updateDepositOi.Parameters.AddWithValue("company_id", companyId.Value);
            updateDepositOi.Parameters.AddWithValue("id", deposit.OpenItemId);
            updateDepositOi.Parameters.AddWithValue("open_amount", depositRemaining);
            updateDepositOi.Parameters.AddWithValue("status", depositStatus);
            await updateDepositOi.ExecuteNonQueryAsync(cancellationToken);
        }

        // Mirror the deposit-row status from the open item.
        await using (var updateDeposit = connection.CreateCommand())
        {
            updateDeposit.Transaction = transaction;
            updateDeposit.CommandText = """
                update customer_deposits
                  set status = @status, updated_at = now()
                where company_id = @company_id and id = @id;
                """;
            updateDeposit.Parameters.AddWithValue("company_id", companyId.Value);
            updateDeposit.Parameters.AddWithValue("id", deposit.DepositId);
            updateDeposit.Parameters.AddWithValue("status", depositStatus);
            await updateDeposit.ExecuteNonQueryAsync(cancellationToken);
        }

        // Target invoice open item: reduce by applied amount, flip status.
        var invoiceRemaining = Math.Round(invoiceOpenRemaining - applyAmount, 6, MidpointRounding.ToEven);
        var invoiceStatus = invoiceRemaining <= 0m ? "closed" : "partially_applied";
        await using (var updateInvoiceOi = connection.CreateCommand())
        {
            updateInvoiceOi.Transaction = transaction;
            updateInvoiceOi.CommandText = """
                update ar_open_items
                  set open_amount_tx = @open_amount,
                      open_amount_base = @open_amount,
                      status = @status,
                      updated_at = now()
                where company_id = @company_id and id = @id;
                """;
            updateInvoiceOi.Parameters.AddWithValue("company_id", companyId.Value);
            updateInvoiceOi.Parameters.AddWithValue("id", invoiceOpenItemId);
            updateInvoiceOi.Parameters.AddWithValue("open_amount", invoiceRemaining);
            updateInvoiceOi.Parameters.AddWithValue("status", invoiceStatus);
            await updateInvoiceOi.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private readonly record struct InvoiceContext(
        Guid? SalesOrderId,
        Guid CustomerId,
        decimal InvoiceTotalTx,
        decimal InvoiceFxRate,
        string BaseCurrency,
        Guid? InvoiceOpenItemId,
        decimal InvoiceOpenAmountBase,
        decimal SoTotalTx,
        decimal SoFxRate,
        Guid ArAccountId,
        Guid CustomerDepositAccountId);

    private readonly record struct DepositRow(
        Guid DepositId,
        string DisplayNumber,
        decimal OriginalAmountBase,
        string Status,
        Guid OpenItemId,
        decimal OpenAmountBase);
}
