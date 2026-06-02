using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class PostgreSqlPayBillDraftIdempotencyIntegrationTests
{
    private static readonly CompanyId CompanyId = CompanyId.FromOrdinal(1);
    private static readonly Guid VendorId = Guid.Parse("96000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PreparePayBillDraft_WithSameClientRequestId_ReturnsExistingDraftAndRejectsChangedIntent()
    {
        var connectionString = GetPostgreSqlConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var connectionFactory = new PostgresConnectionFactory(connectionString);
        var executionContext = new PostgresExecutionContextAccessor();
        var handler = new PreparePayBillDraftCommandHandler(
            new PostgresPayBillDocumentRepository(connectionFactory, executionContext),
            new PostgresUnitOfWork(connectionFactory, executionContext));

        await EnsurePayBillClientRequestSchemaAsync(connectionString);

        var clientRequestId = Guid.NewGuid();
        Guid documentId = Guid.Empty;
        Guid bankAccountId = Guid.Empty;
        Guid billId = Guid.Empty;
        Guid openItemId = Guid.Empty;
        UserId userId = default;
        var createdUser = false;
        var createdVendor = false;

        try
        {
            (userId, createdUser) = await GetOrCreateUserAsync(connectionString);
            createdVendor = await EnsureVendorAsync(connectionString);
            bankAccountId = await CreateBankAccountAsync(connectionString);
            (billId, openItemId) = await SeedBillAndOpenItemAsync(connectionString, userId);

            var command = new PreparePayBillDraftCommand(
                CompanyId,
                userId,
                VendorId,
                bankAccountId,
                new DateOnly(2026, 4, 14),
                null,
                "AP idempotency test",
                [new SettlementDraftLine(openItemId, 50m)],
                clientRequestId);

            var first = await handler.HandleAsync(command, CancellationToken.None);
            documentId = first.DocumentId;
            var second = await handler.HandleAsync(command, CancellationToken.None);

            Assert.Equal(first.DocumentId, second.DocumentId);
            Assert.Equal(first.DisplayNumber, second.DisplayNumber);
            Assert.Equal(1, await CountPayBillsForClientRequestAsync(connectionString, clientRequestId));

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
            await CleanupOpenItemAndBillAsync(connectionString, openItemId, billId);
            await CleanupBankAccountAsync(connectionString, bankAccountId);
            await CleanupVendorAsync(connectionString, createdVendor);
            await CleanupUserAsync(connectionString, userId, createdUser);
        }
    }

    private static async Task EnsurePayBillClientRequestSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            alter table pay_bills
              add column if not exists client_request_id uuid null,
              add column if not exists client_request_hash text null;

            create unique index if not exists ux_pay_bills_company_client_request
              on pay_bills (company_id, client_request_id)
              where client_request_id is not null;
            """;
        await command.ExecuteNonQueryAsync();
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
        insertCommand.Parameters.AddWithValue("email", $"paybill-idempotency-{newUserId.Value}@aiseworks.local");
        insertCommand.Parameters.AddWithValue("username", $"paybill-idempotency-{newUserId.Value}");
        insertCommand.Parameters.AddWithValue("password_hash", "integration-test-hash");
        await insertCommand.ExecuteNonQueryAsync();
        return (newUserId, true);
    }

    private static async Task<bool> EnsureVendorAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var findCommand = connection.CreateCommand())
        {
            findCommand.CommandText =
                """
                select 1
                from vendors
                where company_id = @company_id
                  and id = @vendor_id
                limit 1;
                """;
            findCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            findCommand.Parameters.AddWithValue("vendor_id", VendorId);
            if (await findCommand.ExecuteScalarAsync() is not null)
            {
                return false;
            }
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into vendors (
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
        insertCommand.Parameters.AddWithValue("id", VendorId);
        insertCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
        insertCommand.Parameters.AddWithValue("entity_number", "EN2026PBIVD");
        insertCommand.Parameters.AddWithValue("display_name", "Pay bill idempotency vendor");
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
        command.Parameters.AddWithValue("code", $"PBIDEMP-{suffix}");
        command.Parameters.AddWithValue("name", "Pay bill idempotency test bank");
        await command.ExecuteNonQueryAsync();
        return accountId;
    }

    private static async Task<(Guid BillId, Guid OpenItemId)> SeedBillAndOpenItemAsync(
        string connectionString,
        UserId userId)
    {
        var billId = Guid.NewGuid();
        var openItemId = Guid.NewGuid();
        var suffix = billId.ToString("N")[..5].ToUpperInvariant();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var billCommand = connection.CreateCommand())
        {
            billCommand.Transaction = transaction;
            billCommand.CommandText =
                """
                insert into bills (
                  id, company_id, entity_number, bill_number, vendor_id,
                  bill_date, due_date,
                  document_currency_code, base_currency_code,
                  fx_requested_date, fx_effective_date,
                  created_by_user_id
                )
                values (
                  @id, @company_id, @entity_number, @bill_number, @vendor_id,
                  @bill_date, @due_date,
                  'USD', 'USD',
                  @bill_date, @bill_date,
                  @user_id
                );
                """;
            billCommand.Parameters.AddWithValue("id", billId);
            billCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            billCommand.Parameters.AddWithValue("entity_number", $"EN2026{suffix}");
            billCommand.Parameters.AddWithValue("bill_number", $"PBIDEMP-{suffix}");
            billCommand.Parameters.AddWithValue("vendor_id", VendorId);
            billCommand.Parameters.AddWithValue("bill_date", new DateOnly(2026, 4, 14));
            billCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            billCommand.Parameters.AddWithValue("user_id", userId.Value);
            await billCommand.ExecuteNonQueryAsync();
        }

        await using (var openItemCommand = connection.CreateCommand())
        {
            openItemCommand.Transaction = transaction;
            openItemCommand.CommandText =
                """
                insert into ap_open_items (
                  id, company_id, vendor_id, source_type, source_id,
                  balance_side,
                  document_currency_code, base_currency_code,
                  original_amount_tx, original_amount_base,
                  open_amount_tx, open_amount_base,
                  status, due_date
                )
                values (
                  @id, @company_id, @vendor_id, 'bill', @source_id,
                  'credit',
                  'USD', 'USD',
                  100, 100,
                  100, 100,
                  'open', @due_date
                );
                """;
            openItemCommand.Parameters.AddWithValue("id", openItemId);
            openItemCommand.Parameters.AddWithValue("company_id", CompanyId.Value);
            openItemCommand.Parameters.AddWithValue("vendor_id", VendorId);
            openItemCommand.Parameters.AddWithValue("source_id", billId);
            openItemCommand.Parameters.AddWithValue("due_date", new DateOnly(2026, 5, 14));
            await openItemCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return (billId, openItemId);
    }

    private static async Task<int> CountPayBillsForClientRequestAsync(string connectionString, Guid clientRequestId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select count(*)
            from pay_bills
            where company_id = @company_id
              and client_request_id = @client_request_id;
            """;
        command.Parameters.AddWithValue("company_id", CompanyId.Value);
        command.Parameters.AddWithValue("client_request_id", clientRequestId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

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
            lineCommand.CommandText = "delete from pay_bill_lines where pay_bill_id = @document_id;";
            lineCommand.Parameters.AddWithValue("document_id", documentId);
            await lineCommand.ExecuteNonQueryAsync();
        }

        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.Transaction = transaction;
            headerCommand.CommandText = "delete from pay_bills where id = @document_id;";
            headerCommand.Parameters.AddWithValue("document_id", documentId);
            await headerCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task CleanupOpenItemAndBillAsync(string connectionString, Guid openItemId, Guid billId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (openItemId != Guid.Empty)
        {
            await using var deleteOpenItem = connection.CreateCommand();
            deleteOpenItem.Transaction = transaction;
            deleteOpenItem.CommandText = "delete from ap_open_items where id = @id;";
            deleteOpenItem.Parameters.AddWithValue("id", openItemId);
            await deleteOpenItem.ExecuteNonQueryAsync();
        }

        if (billId != Guid.Empty)
        {
            await using var deleteBill = connection.CreateCommand();
            deleteBill.Transaction = transaction;
            deleteBill.CommandText = "delete from bills where id = @id;";
            deleteBill.Parameters.AddWithValue("id", billId);
            await deleteBill.ExecuteNonQueryAsync();
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

    private static async Task CleanupVendorAsync(string connectionString, bool created)
    {
        if (!created)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from vendors where id = @vendor_id;";
        command.Parameters.AddWithValue("vendor_id", VendorId);
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
