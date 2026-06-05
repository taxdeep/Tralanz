using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP.Expenses;
using Modules.AP.Expenses;
using Npgsql;
using SharedKernel.Identity;

namespace Tests.AP;

public sealed class ExpensePostingLedgerSmokeTests
{
    [SkippableFact]
    public async Task CreateAsync_ForeignCurrencyExpense_WritesBalancedBaseLedger()
    {
        var connectionString = GetConnectionString();
        var connectionFactory = new PostgreSqlConnectionFactory(connectionString);
        var store = new PostgreSqlExpenseStore(connectionFactory);
        var (companyId, baseCurrencyCode) = await ReadFirstCompanyAsync(connectionString, CancellationToken.None);
        var transactionCurrencyCode = string.Equals(baseCurrencyCode, "USD", StringComparison.Ordinal)
            ? "CAD"
            : "USD";
        var userId = UserId.FromOrdinal(71);
        var paymentAccountId = Guid.NewGuid();
        var expenseAccountId = Guid.NewGuid();
        Guid expenseId = Guid.Empty;

        try
        {
            await EnsureUserAsync(connectionString, userId, CancellationToken.None);
            await InsertAccountAsync(
                connectionString,
                companyId,
                paymentAccountId,
                code: $"T-BANK-{Random.Shared.Next(100000, 999999)}",
                name: "FX Smoke Bank",
                rootType: "asset",
                detailType: "bank",
                CancellationToken.None);
            await InsertAccountAsync(
                connectionString,
                companyId,
                expenseAccountId,
                code: $"T-EXP-{Random.Shared.Next(100000, 999999)}",
                name: "FX Smoke Office Expense",
                rootType: "expense",
                detailType: "office_expense",
                CancellationToken.None);

            var saved = await store.CreateAsync(
                companyId,
                userId,
                new ExpenseUpsertInput(
                    PayeeKind: ExpensePayeeKind.Other,
                    PayeeId: null,
                    PayeeNameFreeform: "FX smoke payee",
                    PaymentAccountId: paymentAccountId,
                    PaymentMethod: ExpensePaymentMethod.Other,
                    ChequeNumber: null,
                    RefNo: "FX-SMOKE",
                    TransactionCurrencyCode: transactionCurrencyCode,
                    FxRate: 1.3m,
                    PaymentDate: new DateOnly(2026, 5, 15),
                    SourcePurchaseOrderId: null,
                    SourcePurchaseOrderNumber: null,
                    TaxMode: ExpenseTaxMode.Exclusive,
                    DiscountKind: null,
                    DiscountValue: null,
                    Memo: "1 foreign-currency unit smoke expense",
                    InternalNote: null,
                    Lines:
                    [
                        new ExpenseLineInput(
                            Sequence: 1,
                            ServiceDate: null,
                            ItemId: null,
                            ExpenseAccountId: expenseAccountId,
                            Description: "Pen",
                            Quantity: 1m,
                            UnitPrice: 1m,
                            TaxCodeId: null)
                    ]),
                CancellationToken.None);
            expenseId = saved.Id;

            Assert.Equal(1m, saved.TotalAmount);
            Assert.Equal(1.3m, saved.FxRate);
            Assert.NotNull(saved.PostedJournalEntryId);

            var lines = await ReadLedgerLinesAsync(connectionString, companyId, saved.PostedJournalEntryId!.Value, CancellationToken.None);
            Assert.Equal(2, lines.Count);

            var expenseLine = Assert.Single(lines, line => line.AccountId == expenseAccountId);
            Assert.Equal(1m, expenseLine.TxDebit);
            Assert.Equal(0m, expenseLine.TxCredit);
            Assert.Equal(1.30m, expenseLine.Debit);
            Assert.Equal(0m, expenseLine.Credit);

            var paymentLine = Assert.Single(lines, line => line.AccountId == paymentAccountId);
            Assert.Equal(0m, paymentLine.TxDebit);
            Assert.Equal(1m, paymentLine.TxCredit);
            Assert.Equal(0m, paymentLine.Debit);
            Assert.Equal(1.30m, paymentLine.Credit);
        }
        finally
        {
            await CleanupAsync(connectionString, companyId, expenseId, paymentAccountId, expenseAccountId, userId, CancellationToken.None);
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    private static async Task<(CompanyId CompanyId, string BaseCurrencyCode)> ReadFirstCompanyAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, base_currency_code
              FROM companies
             ORDER BY created_at, id
             LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("A seeded company is required for expense posting smoke tests.");
        }

        return (CompanyId.Parse(reader.GetString(0)), reader.GetString(1).Trim().ToUpperInvariant());
    }

    private static async Task EnsureUserAsync(
        string connectionString,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (id, email, username, password_hash, status)
            VALUES (@id, @email, @username, @password_hash, 'active')
            ON CONFLICT (id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("id", userId.Value);
        command.Parameters.AddWithValue("email", $"expense-fx-smoke-{userId.Value}@tralanz.local");
        command.Parameters.AddWithValue("username", $"expense-fx-smoke-{userId.Value}");
        command.Parameters.AddWithValue("password_hash", "test-hash");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAccountAsync(
        string connectionString,
        CompanyId companyId,
        Guid accountId,
        string code,
        string name,
        string rootType,
        string detailType,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (
              id, company_id, entity_number, code, name,
              root_type, detail_type,
              is_active, is_system, is_system_default,
              allow_manual_posting,
              created_at, updated_at
            )
            VALUES (
              @id, @company_id, @entity_number, @code, @name,
              @root_type, @detail_type,
              TRUE, FALSE, FALSE,
              TRUE,
              NOW(), NOW()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", EntityNumber.Create(2026, Random.Shared.NextInt64(1, EntityNumber.MaxOrdinal)).Value);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("root_type", rootType);
        command.Parameters.AddWithValue("detail_type", detailType);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<LedgerLine>> ReadLedgerLinesAsync(
        string connectionString,
        CompanyId companyId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        var rows = new List<LedgerLine>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id, tx_debit, tx_credit, debit, credit
              FROM ledger_entries
             WHERE company_id = @company_id
               AND journal_entry_id = @journal_entry_id
             ORDER BY debit DESC, credit DESC;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("journal_entry_id", journalEntryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LedgerLine(
                reader.GetGuid(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4)));
        }

        return rows;
    }

    private static async Task CleanupAsync(
        string connectionString,
        CompanyId companyId,
        Guid expenseId,
        Guid paymentAccountId,
        Guid expenseAccountId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM expense_lines WHERE expense_id = @expense_id;
            DELETE FROM expenses WHERE id = @expense_id AND company_id = @company_id;
            DELETE FROM journal_entries WHERE company_id = @company_id AND source_id = @expense_id;
            DELETE FROM accounts WHERE company_id = @company_id AND id IN (@payment_account_id, @expense_account_id);
            DELETE FROM users WHERE id = @user_id AND email LIKE 'expense-fx-smoke-%';
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("expense_id", expenseId);
        command.Parameters.AddWithValue("payment_account_id", paymentAccountId);
        command.Parameters.AddWithValue("expense_account_id", expenseAccountId);
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record LedgerLine(
        Guid AccountId,
        decimal TxDebit,
        decimal TxCredit,
        decimal Debit,
        decimal Credit);
}
