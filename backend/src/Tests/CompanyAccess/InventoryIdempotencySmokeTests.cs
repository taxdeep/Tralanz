using Citus.Modules.Inventory.Application.Contracts;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Inventory;
using Npgsql;

namespace Tests.CompanyAccess;

/// <summary>
/// Integration smoke tests for the PR-5 idempotency foundation on
/// <c>inventory_documents</c>. The migration adds an
/// <c>idempotency_key</c> column + partial unique index per
/// (company, key). The four POST stores (Receipt, Issue, Shipment,
/// Transfer) persist the key on INSERT and translate the resulting
/// 23505 unique violation into a typed
/// <see cref="InventoryIdempotencyReplayException"/>.
///
/// These tests don't depend on the workflow / endpoint layer — they
/// exercise the schema + helper directly by INSERTing rows into
/// <c>inventory_documents</c> and verifying that the second INSERT
/// with the same key surfaces as a replay exception, not a silent
/// duplicate. That's the core invariant; once HTTP callers pass
/// keys, the protection chains through automatically.
/// </summary>
public sealed class InventoryIdempotencySmokeTests
{
    [SkippableFact]
    public async Task SameCompany_SameKey_SecondInsertRaisesIdempotencyViolation()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            const string key = "smoke-key-001";

            // First insert succeeds.
            await InsertReceiptDocumentAsync(seed, key);

            // Second insert with the same (company, key) must hit the
            // partial unique index → 23505 with the index name in the
            // constraint payload. We catch and assert via the helper
            // so a future rename of the index is caught here too.
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertReceiptDocumentAsync(seed, key));
            Assert.True(InventoryIdempotencyHelper.IsIdempotencyViolation(ex),
                $"Expected idempotency violation, got SqlState={ex.SqlState} ConstraintName='{ex.ConstraintName}'");

            // The helper resolves the existing row and throws the
            // typed replay exception with the original document_id.
            var replay = await Assert.ThrowsAsync<InventoryIdempotencyReplayException>(() =>
                InventoryIdempotencyHelper.ThrowReplayAsync(
                    seed.Connections, seed.CompanyId, key, CancellationToken.None));
            Assert.Equal(key, replay.IdempotencyKey);
            Assert.NotEqual(Guid.Empty, replay.ExistingDocumentId);
            Assert.Equal("purchase_receipt", replay.DocumentType);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task DifferentCompanies_SameKey_BothInsertsSucceed()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            const string key = "shared-key-across-companies";

            // First company A persists the key. The partial unique
            // index is per-company so the same key in company B must
            // not collide.
            await InsertReceiptDocumentAsync(seed, key);
            await InsertReceiptDocumentAsync(seed, key, useSecondCompany: true);

            // Verify both rows exist with the same key.
            var count = await CountDocumentsByKeyAsync(seed, key);
            Assert.Equal(2, count);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task NullKey_NoConstraintApplied_MultipleInsertsSucceed()
    {
        var seed = await SeedScenarioAsync();
        try
        {
            // The partial index has `where idempotency_key is not null`.
            // Legacy callers that don't send a key (or pass null)
            // continue working unchanged — no protection, but no
            // regression either.
            await InsertReceiptDocumentAsync(seed, idempotencyKey: null);
            await InsertReceiptDocumentAsync(seed, idempotencyKey: null);
            await InsertReceiptDocumentAsync(seed, idempotencyKey: null);

            var count = await CountDocumentsByKeyAsync(seed, null);
            Assert.Equal(3, count);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    // ---------------------------------------------------------------
    // Seed helpers
    // ---------------------------------------------------------------

    private sealed record SeedScenario(
        PostgreSqlConnectionFactory Connections,
        CompanyId CompanyId,
        CompanyId SecondCompanyId,
        UserId OwnerUserId);

    private static async Task<SeedScenario> SeedScenarioAsync()
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());

        var rand = Random.Shared.Next(5000, 5999);
        var companyId = CompanyId.FromOrdinal(rand);
        var secondCompanyId = CompanyId.FromOrdinal(rand + 1);
        var ownerUserId = UserId.FromOrdinal(rand);

        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            -- The migration runner has already created inventory_documents
            -- with the idempotency_key column + partial unique index.
            -- These tests only need a base companies + users + memberships
            -- skeleton so the FKs on inventory_documents row resolve.
            insert into companies (
              id, entity_number, legal_name, base_currency_code,
              multi_currency_enabled, status
            ) values
              (@company_id,        @entity_number_a, 'IdempotencySmokeTest Co. A', 'USD', false, 'active'),
              (@second_company_id, @entity_number_b, 'IdempotencySmokeTest Co. B', 'USD', false, 'active');

            insert into users (id, email, username, password_hash, status)
            values (@owner_id, @owner_email, 'owner.idem', 'hashed', 'active');
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("second_company_id", secondCompanyId.Value);
        command.Parameters.AddWithValue("entity_number_a", BuildEntityNumber());
        command.Parameters.AddWithValue("entity_number_b", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_id", ownerUserId.Value);
        command.Parameters.AddWithValue("owner_email", $"{ownerUserId.Value}@idempotency.test");
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(connections, companyId, secondCompanyId, ownerUserId);
    }

    private static async Task InsertReceiptDocumentAsync(
        SeedScenario seed,
        string? idempotencyKey,
        bool useSecondCompany = false)
    {
        var companyId = useSecondCompany ? seed.SecondCompanyId : seed.CompanyId;

        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into inventory_documents (
              id, company_id, document_number, document_type, status,
              movement_direction, posting_date, created_by_user_id,
              created_at, posted_at, idempotency_key
            ) values (
              gen_random_uuid(), @company_id, @document_number,
              'purchase_receipt', 'posted', 'inbound', current_date,
              @created_by_user_id, now(), now(), @idempotency_key
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue(
            "document_number", $"PR-IDEM-{Guid.NewGuid():N}".Substring(0, 16));
        command.Parameters.AddWithValue("created_by_user_id", seed.OwnerUserId.Value);
        command.Parameters.AddWithValue(
            "idempotency_key",
            idempotencyKey is null ? (object)DBNull.Value : idempotencyKey);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<int> CountDocumentsByKeyAsync(SeedScenario seed, string? key)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = key is null
            ? """
                select count(*)::int from inventory_documents
                 where company_id in (@company_id, @second_company_id)
                   and idempotency_key is null;
                """
            : """
                select count(*)::int from inventory_documents
                 where company_id in (@company_id, @second_company_id)
                   and idempotency_key = @key;
                """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("second_company_id", seed.SecondCompanyId.Value);
        if (key is not null)
        {
            command.Parameters.AddWithValue("key", key);
        }
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0);
    }

    private static async Task CleanupAsync(SeedScenario seed)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from inventory_documents
             where company_id in (@company_id, @second_company_id);
            delete from companies
             where id in (@company_id, @second_company_id);
            delete from users where id = @owner_id;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("second_company_id", seed.SecondCompanyId.Value);
        command.Parameters.AddWithValue("owner_id", seed.OwnerUserId.Value);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    private static string BuildEntityNumber()
    {
        var ordinal = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 60_466_176;
        return EntityNumber.Create(2099, ordinal).Value;
    }
}
