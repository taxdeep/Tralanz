using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnitySearch.Domain.Shared;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.UnitySearch;

namespace Tests.CompanyAccess;

/// <summary>
/// Integration smoke tests for the PR-4D permission filtering in
/// PostgreSqlUnitySearchQueryService. Asserts the contract that:
///
/// <list type="number">
///   <item>Owner bypasses (implied-all): sees AR + AP + GL entities.</item>
///   <item>Non-Owner with only AR view grant: sees AR entities, not AP.</item>
///   <item>Non-Owner with no grants: sees only the no-permission-required
///     "jump_to" / report static rows, no business entities.</item>
/// </list>
///
/// The test seeds three search_documents rows (one invoice, one bill,
/// one journal entry) directly and exercises the query SQL for three
/// different membership configurations. The projection store / Seed
/// methods are NOT exercised — that's covered by the existing
/// PostgreSqlUnitySearchProjectionStore tests.
/// </summary>
public sealed class UnitySearchPermissionFilterSmokeTests
{
    [SkippableFact]
    public async Task Owner_SeesAllEntities_RegardlessOfRequiredPermissions()
    {
        var seed = await SeedScenarioAsync();
        var service = new PostgreSqlUnitySearchQueryService(seed.Connections);

        try
        {
            var rows = await service.SearchDocumentsAsync(
                BuildQuery(seed.CompanyId, seed.OwnerUserId),
                BuildPolicy(),
                normalizedQuery: "smoke",
                BuildEmptyHints(),
                CancellationToken.None);

            var entityTypes = rows.Select(r => r.EntityType).Distinct().OrderBy(s => s).ToArray();
            Assert.Contains("invoice", entityTypes);
            Assert.Contains("bill", entityTypes);
            Assert.Contains("journal_entry", entityTypes);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task NonOwnerWithOnlyArInvoiceView_SeesInvoicesNotBills()
    {
        var seed = await SeedScenarioAsync();
        await GrantTokenAsync(seed, seed.UserAUserId, "ar.invoice.view");
        var service = new PostgreSqlUnitySearchQueryService(seed.Connections);

        try
        {
            var rows = await service.SearchDocumentsAsync(
                BuildQuery(seed.CompanyId, seed.UserAUserId),
                BuildPolicy(),
                normalizedQuery: "smoke",
                BuildEmptyHints(),
                CancellationToken.None);

            var entityTypes = rows.Select(r => r.EntityType).Distinct().OrderBy(s => s).ToArray();
            Assert.Contains("invoice", entityTypes);
            Assert.DoesNotContain("bill", entityTypes);
            Assert.DoesNotContain("journal_entry", entityTypes);
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    [SkippableFact]
    public async Task NonOwnerWithNoGrants_SeesNoBusinessEntities()
    {
        var seed = await SeedScenarioAsync();
        // User B has zero grants — not even auto-migrated tokens
        // (this test bypasses PR-4A's safe-allowlist by not seeding
        // any company_user_permissions for User B).
        var service = new PostgreSqlUnitySearchQueryService(seed.Connections);

        try
        {
            var rows = await service.SearchDocumentsAsync(
                BuildQuery(seed.CompanyId, seed.UserBUserId),
                BuildPolicy(),
                normalizedQuery: "smoke",
                BuildEmptyHints(),
                CancellationToken.None);

            var entityTypes = rows.Select(r => r.EntityType).Distinct().ToArray();
            Assert.DoesNotContain("invoice", entityTypes);
            Assert.DoesNotContain("bill", entityTypes);
            Assert.DoesNotContain("journal_entry", entityTypes);
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
        UserId OwnerUserId,
        UserId UserAUserId,
        UserId UserBUserId,
        Guid InvoiceSourceId,
        Guid BillSourceId,
        Guid JeSourceId);

    private static async Task<SeedScenario> SeedScenarioAsync()
    {
        var connections = new PostgreSqlConnectionFactory(GetConnectionString());

        var rand = Random.Shared.Next(3000, 3999);
        var companyId = CompanyId.FromOrdinal(rand);
        var ownerUserId = UserId.FromOrdinal(rand);
        var userAUserId = UserId.FromOrdinal(rand + 1);
        var userBUserId = UserId.FromOrdinal(rand + 2);
        var invoiceSourceId = Guid.NewGuid();
        var billSourceId = Guid.NewGuid();
        var jeSourceId = Guid.NewGuid();

        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            alter table company_memberships
              add column if not exists permissions jsonb not null default '[]'::jsonb;
            alter table company_memberships
              add column if not exists is_owner boolean not null default false;

            insert into companies (
              id, entity_number, legal_name, base_currency_code,
              multi_currency_enabled, status
            ) values (
              @company_id, @entity_number, 'PermFilterSmoke Co.',
              'USD', false, 'active'
            );

            -- X-4 test-isolation: append per-run UserId for unique username.
            insert into users (id, email, username, password_hash, status)
            values
              (@owner_id,  @owner_email,  'owner.permfilter.'  || @owner_id,  'hashed', 'active'),
              (@user_a_id, @user_a_email, 'user.a.permfilter.' || @user_a_id, 'hashed', 'active'),
              (@user_b_id, @user_b_email, 'user.b.permfilter.' || @user_b_id, 'hashed', 'active');

            insert into company_memberships (
              id, company_id, user_id, role, permissions, is_active, is_owner
            ) values
              (gen_random_uuid(), @company_id, @owner_id,  'owner', '[]'::jsonb, true, true),
              (gen_random_uuid(), @company_id, @user_a_id, 'user',  '[]'::jsonb, true, false),
              (gen_random_uuid(), @company_id, @user_b_id, 'user',  '[]'::jsonb, true, false);

            -- Three search_documents rows, one per entity_type we're
            -- gating. Each carries the matching new fine-grained
            -- required_permission token from PR-4D.
            insert into search_documents (
              company_id, entity_type, source_id, group_key, primary_text,
              secondary_text, search_text, exact_code_norm, navigation_href,
              metadata_json, is_active, is_voided, rank_boost, version,
              module_key, required_permissions, visibility_scope
            ) values
              (@company_id, 'invoice', @invoice_id, 'transactions', 'smoke-inv-1',
               'smoke', 'smoke-inv-1 smoke', 'smoke-inv-1', '/invoices/x',
               '{}'::jsonb, true, false, 40, 1,
               'ar', array['ar.invoice.view']::text[], 'company'),
              (@company_id, 'bill', @bill_id, 'transactions', 'smoke-bill-1',
               'smoke', 'smoke-bill-1 smoke', 'smoke-bill-1', '/bills/x',
               '{}'::jsonb, true, false, 40, 1,
               'ap', array['ap.bill.view']::text[], 'company'),
              (@company_id, 'journal_entry', @je_id, 'transactions', 'smoke-je-1',
               'smoke', 'smoke-je-1 smoke', 'smoke-je-1', '/journals/x',
               '{}'::jsonb, true, false, 38, 1,
               'gl', array['gl.journal.view']::text[], 'company');
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", BuildEntityNumber());
        command.Parameters.AddWithValue("owner_id", ownerUserId.Value);
        command.Parameters.AddWithValue("user_a_id", userAUserId.Value);
        command.Parameters.AddWithValue("user_b_id", userBUserId.Value);
        command.Parameters.AddWithValue("owner_email",  $"{ownerUserId.Value}@permfilter.test");
        command.Parameters.AddWithValue("user_a_email", $"{userAUserId.Value}@permfilter.test");
        command.Parameters.AddWithValue("user_b_email", $"{userBUserId.Value}@permfilter.test");
        command.Parameters.AddWithValue("invoice_id", invoiceSourceId);
        command.Parameters.AddWithValue("bill_id", billSourceId);
        command.Parameters.AddWithValue("je_id", jeSourceId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return new SeedScenario(
            connections, companyId,
            ownerUserId, userAUserId, userBUserId,
            invoiceSourceId, billSourceId, jeSourceId);
    }

    private static async Task GrantTokenAsync(SeedScenario seed, UserId userId, string token)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into company_user_permissions
              (company_id, user_id, permission_token, granted_by_user_id, is_active)
            values
              (@company_id, @user_id, @token, @granted_by, true)
            on conflict do nothing;
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("granted_by", seed.OwnerUserId.Value);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static UnitySearchQuery BuildQuery(CompanyId companyId, UserId userId) => new()
    {
        CompanyId = companyId,
        UserId = userId,
        Context = SearchScopeContext.GlobalTopbar,
        SearchText = "smoke",
        Take = 50,
        // Permissions intentionally empty — PR-4D's caller_info CTE
        // pulls them fresh from company_user_permissions and ignores
        // this list (legacy contract field, kept for compat).
        Permissions = Array.Empty<string>(),
    };

    private static SearchPolicyDefinition BuildPolicy() => new(
        Context: SearchScopeContext.GlobalTopbar,
        EntityTypes: new[] { "invoice", "bill", "journal_entry" },
        EnforceActiveOnly: false,
        EnforceBusinessEligibility: false);

    private static UnitySearchQueryHints BuildEmptyHints() =>
        new(QueryClassTag: "text", NumericValue: null);

    private static async Task CleanupAsync(SeedScenario seed)
    {
        await using var connection = await seed.Connections.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from search_documents where company_id = @company_id;
            delete from company_user_permissions where company_id = @company_id;
            delete from company_memberships where company_id = @company_id;
            delete from companies where id = @company_id;
            delete from users where id = any(@user_ids);
            """;
        command.Parameters.AddWithValue("company_id", seed.CompanyId.Value);
        command.Parameters.AddWithValue("user_ids", new[]
        {
            seed.OwnerUserId.Value, seed.UserAUserId.Value, seed.UserBUserId.Value,
        });
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
