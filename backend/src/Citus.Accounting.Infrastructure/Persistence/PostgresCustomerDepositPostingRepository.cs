using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;

namespace Citus.Accounting.Infrastructure.Persistence;

/// <summary>
/// M5 iter 3: standalone Customer Deposit. Persists customer_deposits
/// + ar_open_items rows in a single scope and returns the
/// <see cref="CustomerDepositPostingDocument"/> the engine journalises
/// (Dr Bank / Cr Customer Deposit). The caller (handler) wraps prepare
/// + post in a unit of work so a posting failure rolls both back.
///
/// V1 simplification: deposit currency must equal company base currency
/// — multi-currency FX wiring lands in a follow-up iter (mirrors the
/// SR / RR pattern of fx_rate=1 short-circuit when same-currency).
/// </summary>
public sealed class PostgresCustomerDepositPostingRepository : ICustomerDepositPostingRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresCustomerDepositPostingRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<CustomerDepositPostingPreparation> PreparePostingDocumentAsync(
        CompanyId companyId,
        UserId userId,
        CustomerDepositPostingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SalesOrderId == Guid.Empty)
            throw new ArgumentException("Sales-order id is required.", nameof(request));
        if (request.CustomerId == Guid.Empty)
            throw new ArgumentException("Customer id is required.", nameof(request));
        if (request.DepositToAccountId == Guid.Empty)
            throw new ArgumentException("Deposit-to (bank) account id is required.", nameof(request));
        if (request.AmountTx <= 0m)
            throw new InvalidOperationException("Deposit amount must be positive.");

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Resolve currencies + customer deposit account in one round-trip.
        var resolution = await ResolveAccountsAsync(connection, transaction, companyId, request.SalesOrderId, cancellationToken);
        if (resolution is null)
        {
            throw new InvalidOperationException(
                $"Sales order {request.SalesOrderId:D} was not found for company {companyId.Value:D}.");
        }

        if (!string.Equals(resolution.Value.SoCurrencyCode, resolution.Value.BaseCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Multi-currency customer deposits are not supported in V1. Sales order is in {resolution.Value.SoCurrencyCode}, " +
                $"company base is {resolution.Value.BaseCurrencyCode}. Use a same-currency deposit or wait for the FX wiring.");
        }

        if (resolution.Value.CustomerDepositAccountId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Chart of Accounts is missing the Customer Deposit account (system_role='customer_deposit', code 24700). " +
                "Re-seed the standard CoA or add it manually.");
        }

        // Reserve entity / display numbers from the shared sequences.
        var year = request.DocumentDate.Year;
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

        var displayNumber = await PostgresSourceDocumentDraftNumbering.ReserveAsync(
            connection,
            transaction,
            companyId,
            "customer-deposit-display",
            "DEP-",
            6,
            await PostgresSourceDocumentDraftNumbering.FindDisplaySeedNumberAsync(
                connection,
                transaction,
                companyId,
                "customer_deposits",
                "display_number",
                "^DEP-[0-9]+$",
                4,
                cancellationToken),
            cancellationToken);

        // V1 same-currency: tx == base, fx_rate = 1.
        var amountTx = Math.Round(request.AmountTx, 6, MidpointRounding.ToEven);
        var amountBase = amountTx;
        var fxRate = 1m;

        var depositId = Guid.NewGuid();
        await using (var insertDeposit = connection.CreateCommand())
        {
            insertDeposit.Transaction = transaction;
            insertDeposit.CommandText = """
                insert into customer_deposits (
                  id, company_id, customer_id,
                  entity_number, display_number,
                  status, deposit_date,
                  transaction_currency_code, base_currency_code,
                  fx_rate_snapshot_id, fx_rate, fx_requested_date, fx_effective_date, fx_source,
                  original_amount_tx, original_amount_base,
                  source_receive_payment_id, source_sales_order_id,
                  memo, posted_at, created_by_user_id,
                  created_at, updated_at)
                values (
                  @id, @company_id, @customer_id,
                  @entity_number, @display_number,
                  'open', @deposit_date,
                  @currency, @currency,
                  null, @fx_rate, @deposit_date, @deposit_date, 'identity',
                  @amount_tx, @amount_base,
                  null, @sales_order_id,
                  @memo, now(), @user_id,
                  now(), now());
                """;
            insertDeposit.Parameters.AddWithValue("id", depositId);
            insertDeposit.Parameters.AddWithValue("company_id", companyId.Value);
            insertDeposit.Parameters.AddWithValue("customer_id", request.CustomerId);
            insertDeposit.Parameters.AddWithValue("entity_number", entityNumber);
            insertDeposit.Parameters.AddWithValue("display_number", displayNumber);
            insertDeposit.Parameters.AddWithValue("deposit_date", request.DocumentDate);
            insertDeposit.Parameters.AddWithValue("currency", resolution.Value.BaseCurrencyCode);
            insertDeposit.Parameters.AddWithValue("fx_rate", fxRate);
            insertDeposit.Parameters.AddWithValue("amount_tx", amountTx);
            insertDeposit.Parameters.AddWithValue("amount_base", amountBase);
            insertDeposit.Parameters.AddWithValue("sales_order_id", request.SalesOrderId);
            insertDeposit.Parameters.AddWithValue("memo", string.IsNullOrWhiteSpace(request.Memo) ? (object)DBNull.Value : request.Memo.Trim());
            insertDeposit.Parameters.AddWithValue("user_id", userId.Value);
            await insertDeposit.ExecuteNonQueryAsync(cancellationToken);
        }

        // Mirror open-item — drives M5 iter 4 pro-rata clearing.
        await using (var insertOpenItem = connection.CreateCommand())
        {
            insertOpenItem.Transaction = transaction;
            insertOpenItem.CommandText = """
                insert into ar_open_items (
                  id, company_id, customer_id,
                  source_type, source_id, balance_side,
                  document_currency_code, base_currency_code,
                  original_amount_tx, original_amount_base,
                  open_amount_tx, open_amount_base,
                  status, due_date,
                  created_at, updated_at)
                values (
                  gen_random_uuid(), @company_id, @customer_id,
                  'customer_deposit', @source_id, 'credit',
                  @currency, @currency,
                  @amount_tx, @amount_base,
                  @amount_tx, @amount_base,
                  'open', null,
                  now(), now());
                """;
            insertOpenItem.Parameters.AddWithValue("company_id", companyId.Value);
            insertOpenItem.Parameters.AddWithValue("customer_id", request.CustomerId);
            insertOpenItem.Parameters.AddWithValue("source_id", depositId);
            insertOpenItem.Parameters.AddWithValue("currency", resolution.Value.BaseCurrencyCode);
            insertOpenItem.Parameters.AddWithValue("amount_tx", amountTx);
            insertOpenItem.Parameters.AddWithValue("amount_base", amountBase);
            await insertOpenItem.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var document = new CustomerDepositPostingDocument(
            id: depositId,
            companyId: companyId,
            entityNumber: EntityNumber.Parse(entityNumber),
            displayNumber: new DocumentNumber(displayNumber),
            documentDate: request.DocumentDate,
            customerId: request.CustomerId,
            depositToAccountId: request.DepositToAccountId,
            customerDepositAccountId: resolution.Value.CustomerDepositAccountId,
            transactionCurrencyCode: new CurrencyCode(resolution.Value.BaseCurrencyCode),
            baseCurrencyCode: new CurrencyCode(resolution.Value.BaseCurrencyCode),
            fxSnapshot: null,
            amountTx: amountTx,
            amountBase: amountBase,
            memo: request.Memo);

        return new CustomerDepositPostingPreparation(
            Document: document,
            ExistingDepositId: null,
            ExistingDisplayNumber: null);
    }

    private static async Task<AccountResolution?> ResolveAccountsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        CompanyId companyId,
        Guid salesOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select
              so.transaction_currency_code as so_currency,
              c.base_currency_code         as base_currency,
              (
                select a.id
                  from accounts a
                 where a.company_id = c.id
                   and a.system_role = 'customer_deposit'
                   and a.is_active = true
                 order by a.code
                 limit 1
              ) as customer_deposit_account_id
            from sales_orders so
            join companies c on c.id = so.company_id
            where so.company_id = @company_id and so.id = @so_id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("so_id", salesOrderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AccountResolution(
            SoCurrencyCode: reader.GetString(0),
            BaseCurrencyCode: reader.GetString(1),
            CustomerDepositAccountId: reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2));
    }

    private readonly record struct AccountResolution(
        string SoCurrencyCode,
        string BaseCurrencyCode,
        Guid CustomerDepositAccountId);
}
