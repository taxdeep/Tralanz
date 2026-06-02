using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Npgsql;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PostgreSqlReceivePaymentDraftIdempotencyIntegrationTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid CustomerId = Guid.Parse("97000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrepareReceivePaymentDraft_WithSameClientRequestId_ReturnsExistingDraftAndRejectsChangedIntent()
    {
        var connectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var connectionFactory = new PostgresConnectionFactory(connectionString);
        var executionContext = new PostgresExecutionContextAccessor();
        var handler = new PrepareReceivePaymentDraftCommandHandler(
            new PostgresReceivePaymentDocumentRepository(connectionFactory, executionContext),
            new PostgresUnitOfWork(connectionFactory, executionContext));

        await EnsureReceivePaymentClientRequestSchemaAsync(connectionString);

        var clientRequestId = Guid.NewGuid();
        Guid documentId = Guid.Empty;
        Guid bankAccountId = Guid.Empty;
        Guid invoiceId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        UserId userId = default;
        var createdUser = false;
        var createdCustomer = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionString);
            createdCustomer = await EnsureCustomerAsync(connectionString);
            bankAccountId = await CreateBankAccountAsync(connectionString);
            (invoiceId, openItemId) = await SeedInvoiceAndOpenItemAsync(connectionString, userId);

            var command = new PrepareReceivePaymentDraftCommand(
                CompanyId,
                userId,
                CustomerId,
                bankAccountId,
                new DateOnly(2026, 4, 14),
                null,
                "AR idempotency test",
                [new SettlementDraftLine(openItemId, 50m)],
                ClientRequestId: clientRequestId);

            var first = await handler.HandleAsync(command, CancellationToken.None);
            documentId = first.DocumentId;
            var second = await handler.HandleAsync(command, CancellationToken.None);

            Assert.Equal(first.DocumentId, second.DocumentId);
            Assert.Equal(first.DisplayNumber, second.DisplayNumber);
            Assert.Equal(1, await CountReceivePaymentsForClientRequestAsync(connectionString, clientRequestId));

            var changedIntent = command with
            {
                Lines = [new SettlementDraftLine(openItemId, 40m)]
            };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(changedIntent, CancellationToken.None));
        }
        finally
        {
            await CleanupDraftAsync(connectionString, documentId);
            await CleanupOpenItemAndInvoiceAsync(connectionString, openItemId, invoiceId);
            await CleanupBankAccountAsync(connectionString, bankAccountId);
            await CleanupCustomerAsync(connectionString, createdCustomer);
            await CleanupUserAsync(connectionString, userId, createdUser);
        }
    }

    [Fact]
    public async Task PostReceivePayment_WithExtraDeposit_PostsLiabilityAndParksCustomerDeposit()
    {
        var connectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var connectionFactory = new PostgresConnectionFactory(connectionString);
        var executionContext = new PostgresExecutionContextAccessor();
        var documents = new PostgresReceivePaymentDocumentRepository(connectionFactory, executionContext);
        var unitOfWork = new PostgresUnitOfWork(connectionFactory, executionContext);
        var prepareHandler = new PrepareReceivePaymentDraftCommandHandler(
            documents,
            unitOfWork);
        var postingEngine = new DefaultPostingEngine(
            new DefaultPostingValidator(),
            new NullPostingPeriodPolicyValidator(),
            new NullTaxEngine(),
            new LocalFirstFxResolutionService(new PostgresFxSnapshotRepository(connectionFactory, executionContext)),
            new AccountingPostingFragmentBuilder(),
            new DefaultJournalAggregator(),
            new PostgresJournalEntryWriter(connectionFactory, executionContext));
        var postHandler = new PostReceivePaymentCommandHandler(
            documents,
            postingEngine,
            new PostgresSettlementApplicationRepository(connectionFactory, executionContext),
            unitOfWork);

        await EnsureReceivePaymentClientRequestSchemaAsync(connectionString);

        Guid documentId = Guid.Empty;
        Guid bankAccountId = Guid.Empty;
        Guid receivableAccountId = Guid.Empty;
        Guid customerDepositAccountId = Guid.Empty;
        Guid invoiceId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        UserId userId = default;
        var createdUser = false;
        var createdCustomer = false;
        var createdReceivableAccount = false;
        var createdCustomerDepositAccount = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionString);
            createdCustomer = await EnsureCustomerAsync(connectionString);
            (receivableAccountId, createdReceivableAccount) = await EnsureSystemAccountAsync(
                connectionString,
                "accounts_receivable",
                "1100",
                "Accounts Receivable",
                "asset",
                "accounts_receivable");
            (customerDepositAccountId, createdCustomerDepositAccount) = await EnsureSystemAccountAsync(
                connectionString,
                "customer_deposit",
                "24700",
                "Customer Deposits",
                "liability",
                "customer_deposits");
            bankAccountId = await CreateBankAccountAsync(connectionString);
            (invoiceId, openItemId) = await SeedInvoiceAndOpenItemAsync(connectionString, userId);

            var command = new PrepareReceivePaymentDraftCommand(
                CompanyId,
                userId,
                CustomerId,
                bankAccountId,
                new DateOnly(2026, 4, 15),
                null,
                "Overpayment parked as customer deposit",
                [new SettlementDraftLine(openItemId, 50m)],
                ExtraDepositAmount: 5m,
                ClientRequestId: Guid.NewGuid());

            var prepared = await prepareHandler.HandleAsync(command, CancellationToken.None);
            documentId = prepared.DocumentId;
            Assert.Equal(55m, prepared.TotalAmount);

            var posted = await postHandler.HandleAsync(
                new PostReceivePaymentCommand(
                    CompanyId,
                    documentId,
                    userId,
                    null,
                    $"receive-payment-extra-deposit-test:{documentId:D}"),
                CancellationToken.None);

            Assert.NotEqual(Guid.Empty, posted.JournalEntryId);

            var snapshot = await LoadPostedOverpaymentSnapshotAsync(connectionString, documentId, openItemId);
            Assert.Equal(55m, snapshot.BankDebit);
            Assert.Equal(50m, snapshot.ArCredit);
            Assert.Equal(5m, snapshot.CustomerDepositCredit);
            Assert.Equal(5m, snapshot.CustomerDepositAmount);
            Assert.Equal(5m, snapshot.CustomerDepositOpenAmount);
            Assert.Equal("open", snapshot.CustomerDepositStatus);
            Assert.Equal(50m, snapshot.InvoiceOpenAmount);
            Assert.Equal("partially_applied", snapshot.InvoiceOpenStatus);

            var reposted = await postHandler.HandleAsync(
                new PostReceivePaymentCommand(
                    CompanyId,
                    documentId,
                    userId,
                    null,
                    $"receive-payment-extra-deposit-test:{documentId:D}"),
                CancellationToken.None);

            Assert.Equal(posted.JournalEntryId, reposted.JournalEntryId);
            Assert.Equal(1, await CountCustomerDepositsForReceivePaymentAsync(connectionString, documentId));
        }
        finally
        {
            await CleanupPostedReceivePaymentAsync(connectionString, documentId);
            await CleanupOpenItemAndInvoiceAsync(connectionString, openItemId, invoiceId);
            await CleanupBankAccountAsync(connectionString, bankAccountId);
            await CleanupSystemAccountAsync(connectionString, customerDepositAccountId, createdCustomerDepositAccount);
            await CleanupSystemAccountAsync(connectionString, receivableAccountId, createdReceivableAccount);
            await CleanupCustomerAsync(connectionString, createdCustomer);
            await CleanupUserAsync(connectionString, userId, createdUser);
        }
    }

    [Fact]
    public async Task PrepareReceivePaymentDraft_RejectsForeignCurrencyExtraDepositUntilFxDepositApplicationIsImplemented()
    {
        var connectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var connectionFactory = new PostgresConnectionFactory(connectionString);
        var executionContext = new PostgresExecutionContextAccessor();
        var handler = new PrepareReceivePaymentDraftCommandHandler(
            new PostgresReceivePaymentDocumentRepository(connectionFactory, executionContext),
            new PostgresUnitOfWork(connectionFactory, executionContext));

        await EnsureReceivePaymentClientRequestSchemaAsync(connectionString);

        Guid bankAccountId = Guid.Empty;
        Guid invoiceId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        UserId userId = default;
        var createdUser = false;
        var createdCustomer = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionString);
            createdCustomer = await EnsureCustomerAsync(connectionString);
            bankAccountId = await CreateBankAccountAsync(connectionString);
            (invoiceId, openItemId) = await SeedInvoiceAndOpenItemAsync(
                connectionString,
                userId,
                documentCurrencyCode: "CAD",
                baseCurrencyCode: "USD");
            await SeedAcceptedFxSnapshotAsync(connectionString, "USD", "CAD", new DateOnly(2026, 4, 15), 0.75m);

            var command = new PrepareReceivePaymentDraftCommand(
                CompanyId,
                userId,
                CustomerId,
                bankAccountId,
                new DateOnly(2026, 4, 15),
                null,
                "Overpayment should not be silently dropped",
                [new SettlementDraftLine(openItemId, 50m)],
                ExtraDepositAmount: 5m,
                ClientRequestId: Guid.NewGuid());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.HandleAsync(command, CancellationToken.None));
            Assert.Contains("base-currency", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupOpenItemAndInvoiceAsync(connectionString, openItemId, invoiceId);
            await CleanupBankAccountAsync(connectionString, bankAccountId);
            await CleanupCustomerAsync(connectionString, createdCustomer);
            await CleanupUserAsync(connectionString, userId, createdUser);
        }
    }

    private static async Task EnsureReceivePaymentClientRequestSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            alter table receive_payments
              add column if not exists client_request_id uuid null,
              add column if not exists client_request_hash text null;

            create unique index if not exists ux_receive_payments_company_client_request
              on receive_payments (company_id, client_request_id)
              where client_request_id is not null;

            alter table receive_payments
              add column if not exists extra_deposit_amount numeric(20,6) not null default 0;

            create unique index if not exists ux_customer_deposits_company_source_receive_payment
              on customer_deposits (company_id, source_receive_payment_id)
              where source_receive_payment_id is not null;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(Guid AccountId, bool Created)> EnsureSystemAccountAsync(
        string connectionString,
        string systemRole,
        string code,
        string name,
        string rootType,
        string detailType)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText =
                """
                select id
                from accounts
                where company_id = @company_id
                  and system_role = @system_role
                  and is_active = true
                order by code
                limit 1;
                """;
            findCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            findCommand.Parameters.AddWithValue("system_role", systemRole);
            if (await findCommand.ExecuteScalarAsync() is Guid existing)
            {
                return (existing, false);
            }
        }

        var accountId = Guid.NewGuid();
        var suffix = accountId.ToString("N")[..5].ToUpperInvariant();
        var accountCode = $"{code}-RPOV-{suffix}";
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into accounts (
              id,
              company_id,
              entity_number,
              code,
              name,
              root_type,
              detail_type,
              is_active,
              is_system,
              is_system_default,
              system_role,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @code,
              @name,
              @root_type,
              @detail_type,
              true,
              true,
              true,
              @system_role,
              true,
              now(),
              now()
            );
            """;
        insertCommand.Parameters.AddWithValue("id", accountId);
        insertCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
        insertCommand.Parameters.AddWithValue("entity_number", $"EN2026{suffix}");
        insertCommand.Parameters.AddWithValue("code", accountCode);
        insertCommand.Parameters.AddWithValue("name", name);
        insertCommand.Parameters.AddWithValue("root_type", rootType);
        insertCommand.Parameters.AddWithValue("detail_type", detailType);
        insertCommand.Parameters.AddWithValue("system_role", systemRole);
        await insertCommand.ExecuteNonQueryAsync();
        return (accountId, true);
    }

    private static async Task<(UserId UserId, bool Created)> GetOrCreateUserAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText =
                """
                select id
                from users
                order by created_at
                limit 1;
                """;
            var existing = await findCommand.ExecuteScalarAsync();
            if (existing is string userIdString && UserId.TryParse(userIdString, out var userId))
            {
                return (userId, false);
            }
        }

        var newUserId = UserId.FromOrdinal(1);
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into users (
              id,
              email,
              username,
              password_hash,
              status
            )
            values (
              @id,
              @email,
              @username,
              @password_hash,
              'active'
            )
            on conflict (id) do nothing;
            """;
        insertCommand.Parameters.AddWithValue("id", newUserId.Value);
        insertCommand.Parameters.AddWithValue("email", $"receive-payment-idempotency-{newUserId.Value}@aiseworks.local");
        insertCommand.Parameters.AddWithValue("username", $"receive-payment-idempotency-{newUserId.Value}");
        insertCommand.Parameters.AddWithValue("password_hash", "integration-test-hash");
        await insertCommand.ExecuteNonQueryAsync();
        return (newUserId, true);
    }

    private static async Task<bool> EnsureCustomerAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText =
                """
                select 1
                from customers
                where company_id = @company_id
                  and id = @customer_id
                limit 1;
                """;
            findCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            findCommand.Parameters.AddWithValue("customer_id", CustomerId);
            if (await findCommand.ExecuteScalarAsync() is not null)
            {
                return false;
            }
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into customers (
              id,
              company_id,
              entity_number,
              display_name,
              default_currency_code,
              is_active,
              currency_locked
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @display_name,
              'USD',
              true,
              false
            );
            """;
        insertCommand.Parameters.AddWithValue("id", CustomerId);
        insertCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
        insertCommand.Parameters.AddWithValue("entity_number", "EN2026RPICU");
        insertCommand.Parameters.AddWithValue("display_name", "Receive payment idempotency customer");
        await insertCommand.ExecuteNonQueryAsync();
        return true;
    }

    private static async Task<Guid> CreateBankAccountAsync(string connectionString)
    {
        var accountId = Guid.NewGuid();
        var suffix = accountId.ToString("N")[..8];

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into accounts (
              id,
              company_id,
              entity_number,
              code,
              name,
              root_type,
              detail_type,
              is_active,
              is_system,
              is_system_default,
              allow_manual_posting,
              created_at,
              updated_at
            )
            values (
              @id,
              @company_id,
              @entity_number,
              @code,
              @name,
              'asset',
              'bank',
              true,
              false,
              false,
              true,
              now(),
              now()
            );
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("entity_number", $"EN2026{suffix[..5].ToUpperInvariant()}");
        command.Parameters.AddWithValue("code", $"RPIDEMP-{suffix}");
        command.Parameters.AddWithValue("name", "Receive payment idempotency test bank");
        await command.ExecuteNonQueryAsync();
        return accountId;
    }

    private static async Task<(Guid InvoiceId, Guid OpenItemId)> SeedInvoiceAndOpenItemAsync(
        string connectionString,
        UserId userId,
        string documentCurrencyCode = "USD",
        string baseCurrencyCode = "USD")
    {
        var invoiceId = Guid.NewGuid();
        var openItemId = Guid.NewGuid();
        var suffix = invoiceId.ToString("N")[..5].ToUpperInvariant();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var invoiceCommand = connection.CreateCommand())
        {
            invoiceCommand.Transaction = transaction;
            invoiceCommand.CommandText =
                """
                insert into invoices (
                  id, company_id, entity_number, invoice_number, customer_id,
                  invoice_date, due_date,
                  document_currency_code, base_currency_code,
                  fx_requested_date, fx_effective_date,
                  created_by_user_id
                )
                values (
                  @id, @company_id, @entity_number, @invoice_number, @customer_id,
                  @invoice_date, @due_date,
                  @document_currency_code, @base_currency_code,
                  @invoice_date, @invoice_date,
                  @user_id
                );
                """;
            invoiceCommand.Parameters.AddWithValue("id", invoiceId);
            invoiceCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            invoiceCommand.Parameters.AddWithValue("entity_number", $"EN2026{suffix}");
            invoiceCommand.Parameters.AddWithValue("invoice_number", $"RPIDEMP-{suffix}");
            invoiceCommand.Parameters.AddWithValue("customer_id", CustomerId);
            invoiceCommand.Parameters.AddWithValue("invoice_date", new DateOnly(2026, 4, 14));
            invoiceCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            invoiceCommand.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
            invoiceCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            invoiceCommand.Parameters.AddWithValue("user_id", userId.Value);
            await invoiceCommand.ExecuteNonQueryAsync();
        }

        await using (var openItemCommand = connection.CreateCommand())
        {
            openItemCommand.Transaction = transaction;
            openItemCommand.CommandText =
                """
                insert into ar_open_items (
                  id, company_id, customer_id, source_type, source_id,
                  balance_side,
                  document_currency_code, base_currency_code,
                  original_amount_tx, original_amount_base,
                  open_amount_tx, open_amount_base,
                  status, due_date
                )
                values (
                  @id, @company_id, @customer_id, 'invoice', @source_id,
                  'debit',
                  @document_currency_code, @base_currency_code,
                  100, 100,
                  100, 100,
                  'open', @due_date
                );
                """;
            openItemCommand.Parameters.AddWithValue("id", openItemId);
            openItemCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            openItemCommand.Parameters.AddWithValue("customer_id", CustomerId);
            openItemCommand.Parameters.AddWithValue("source_id", invoiceId);
            openItemCommand.Parameters.AddWithValue("document_currency_code", documentCurrencyCode);
            openItemCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            openItemCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            await openItemCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return (invoiceId, openItemId);
    }

    private static async Task SeedAcceptedFxSnapshotAsync(
        string connectionString,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        decimal rate)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into company_fx_rate_snapshots (
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              row_origin,
              snapshot_semantics,
              created_at
            )
            values (
              gen_random_uuid(),
              @company_id,
              @base_currency_code,
              @quote_currency_code,
              @requested_date,
              @requested_date,
              @rate,
              'spot',
              'direct',
              'settlement',
              'settlement',
              'manual',
              'manual',
              now()
            )
            on conflict do nothing;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        command.Parameters.AddWithValue("rate", rate);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostedOverpaymentSnapshot> LoadPostedOverpaymentSnapshotAsync(
        string connectionString,
        Guid receivePaymentId,
        Guid invoiceOpenItemId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        decimal bankDebit;
        decimal arCredit;
        decimal customerDepositCredit;
        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText =
                """
                select
                  coalesce(sum(case when jel.posting_role = 'cash:receipt' then jel.debit else 0 end), 0) as bank_debit,
                  coalesce(sum(case when jel.posting_role = 'control:accounts_receivable' then jel.credit else 0 end), 0) as ar_credit,
                  coalesce(sum(case when jel.posting_role = 'control:customer_deposit' then jel.credit else 0 end), 0) as customer_deposit_credit
                from journal_entries je
                join journal_entry_lines jel on jel.company_id = je.company_id and jel.journal_entry_id = je.id
                where je.company_id = @company_id
                  and je.source_type = 'receive_payment'
                  and je.source_id = @receive_payment_id;
                """;
            journalCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            journalCommand.Parameters.AddWithValue("receive_payment_id", receivePaymentId);
            await using var reader = await journalCommand.ExecuteReaderAsync();
            await reader.ReadAsync();
            bankDebit = reader.GetDecimal(0);
            arCredit = reader.GetDecimal(1);
            customerDepositCredit = reader.GetDecimal(2);
        }

        decimal customerDepositAmount;
        decimal customerDepositOpenAmount;
        string customerDepositStatus;
        await using (var depositCommand = connection.CreateCommand())
        {
            depositCommand.CommandText =
                """
                select
                  cd.original_amount_tx,
                  cd.status,
                  oi.open_amount_tx
                from customer_deposits cd
                join ar_open_items oi
                  on oi.company_id = cd.company_id
                 and oi.source_type = 'customer_deposit'
                 and oi.source_id = cd.id
                where cd.company_id = @company_id
                  and cd.source_receive_payment_id = @receive_payment_id
                limit 1;
                """;
            depositCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            depositCommand.Parameters.AddWithValue("receive_payment_id", receivePaymentId);
            await using var reader = await depositCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            customerDepositAmount = reader.GetDecimal(0);
            customerDepositStatus = reader.GetString(1);
            customerDepositOpenAmount = reader.GetDecimal(2);
        }

        decimal invoiceOpenAmount;
        string invoiceOpenStatus;
        await using (var invoiceOpenItemCommand = connection.CreateCommand())
        {
            invoiceOpenItemCommand.CommandText =
                """
                select open_amount_tx, status
                from ar_open_items
                where company_id = @company_id
                  and id = @open_item_id;
                """;
            invoiceOpenItemCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            invoiceOpenItemCommand.Parameters.AddWithValue("open_item_id", invoiceOpenItemId);
            await using var reader = await invoiceOpenItemCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            invoiceOpenAmount = reader.GetDecimal(0);
            invoiceOpenStatus = reader.GetString(1);
        }

        return new PostedOverpaymentSnapshot(
            bankDebit,
            arCredit,
            customerDepositCredit,
            customerDepositAmount,
            customerDepositOpenAmount,
            customerDepositStatus,
            invoiceOpenAmount,
            invoiceOpenStatus);
    }

    private static async Task<int> CountReceivePaymentsForClientRequestAsync(string connectionString, Guid clientRequestId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)
            from receive_payments
            where company_id = @company_id
              and client_request_id = @client_request_id;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("client_request_id", clientRequestId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountCustomerDepositsForReceivePaymentAsync(
        string connectionString,
        Guid receivePaymentId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)
            from customer_deposits
            where company_id = @company_id
              and source_receive_payment_id = @receive_payment_id;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("receive_payment_id", receivePaymentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed record PostedOverpaymentSnapshot(
        decimal BankDebit,
        decimal ArCredit,
        decimal CustomerDepositCredit,
        decimal CustomerDepositAmount,
        decimal CustomerDepositOpenAmount,
        string CustomerDepositStatus,
        decimal InvoiceOpenAmount,
        string InvoiceOpenStatus);

    private static async Task CleanupDraftAsync(string connectionString, Guid documentId)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.Transaction = transaction;
            lineCommand.CommandText = "delete from receive_payment_lines where receive_payment_id = @document_id;";
            lineCommand.Parameters.AddWithValue("document_id", documentId);
            await lineCommand.ExecuteNonQueryAsync();
        }

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText = "delete from receive_payments where id = @document_id;";
            headerCommand.Parameters.AddWithValue("document_id", documentId);
            await headerCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task CleanupPostedReceivePaymentAsync(string connectionString, Guid documentId)
    {
        if (documentId == Guid.Empty)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var deleteCommands = new[]
        {
            """
            delete from ar_open_items
            where source_type = 'customer_deposit'
              and source_id in (
                select id from customer_deposits where source_receive_payment_id = @document_id
              );
            """,
            "delete from customer_deposits where source_receive_payment_id = @document_id;",
            "delete from settlement_applications where source_type = 'receive_payment' and source_id = @document_id;",
            """
            delete from ledger_entries
            where journal_entry_id in (
              select id from journal_entries where source_type = 'receive_payment' and source_id = @document_id
            );
            """,
            """
            delete from journal_entry_lines
            where journal_entry_id in (
              select id from journal_entries where source_type = 'receive_payment' and source_id = @document_id
            );
            """,
            "delete from journal_entries where source_type = 'receive_payment' and source_id = @document_id;",
            "delete from receive_payment_lines where receive_payment_id = @document_id;",
            "delete from receive_payments where id = @document_id;"
        };

        foreach (var commandText in deleteCommands)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            command.Parameters.AddWithValue("document_id", documentId);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task CleanupOpenItemAndInvoiceAsync(string connectionString, Guid openItemId, Guid invoiceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (openItemId != Guid.Empty)
        {
            await using var deleteOpenItem = connection.CreateCommand();
            deleteOpenItem.Transaction = transaction;
            deleteOpenItem.CommandText = "delete from ar_open_items where id = @id;";
            deleteOpenItem.Parameters.AddWithValue("id", openItemId);
            await deleteOpenItem.ExecuteNonQueryAsync();
        }

        if (invoiceId != Guid.Empty)
        {
            await using var deleteInvoice = connection.CreateCommand();
            deleteInvoice.Transaction = transaction;
            deleteInvoice.CommandText = "delete from invoices where id = @id;";
            deleteInvoice.Parameters.AddWithValue("id", invoiceId);
            await deleteInvoice.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task CleanupBankAccountAsync(string connectionString, Guid bankAccountId)
    {
        if (bankAccountId == Guid.Empty)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from accounts where id = @account_id;";
        command.Parameters.AddWithValue("account_id", bankAccountId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupSystemAccountAsync(string connectionString, Guid accountId, bool created)
    {
        if (!created || accountId == Guid.Empty)
        {
            return;
        }

        await CleanupBankAccountAsync(connectionString, accountId);
    }

    private static async Task CleanupCustomerAsync(string connectionString, bool created)
    {
        if (!created)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from customers where id = @customer_id;";
        command.Parameters.AddWithValue("customer_id", CustomerId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupUserAsync(string connectionString, UserId userId, bool created)
    {
        if (!created || userId.Value is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from users where id = @user_id;";
        command.Parameters.AddWithValue("user_id", userId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string? GetPostgreSqlConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB") ??
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB");
}
