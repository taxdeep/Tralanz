using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.SessionContext;

namespace Tests.CompanyAccess;

public sealed class CompanySessionContextWorkflowTests
{
    [Fact]
    public async Task GetAsync_ReturnsPreferredActiveCompanyFromMembershipTruth()
    {
        var userId = UserId.FromOrdinal(1);
        var firstCompanyId = CompanyId.FromOrdinal(1);
        var secondCompanyId = CompanyId.FromOrdinal(2);
        var firstEntityNumber = BuildEntityNumber();
        var secondEntityNumber = BuildEntityNumber();
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCompanySessionContextStore(connectionFactory);
        var workflow = new CompanySessionContextWorkflow(store);

        try
        {
            await SeedAsync(
                connectionFactory,
                userId,
                firstCompanyId,
                secondCompanyId,
                firstEntityNumber,
                secondEntityNumber,
                CancellationToken.None);

            var context = await workflow.GetAsync(userId, secondCompanyId, CancellationToken.None);

            Assert.NotNull(context);
            Assert.Equal(userId, context!.User.Id);
            Assert.Equal("alice.session", context.User.DisplayName);
            Assert.Equal(secondCompanyId, context.ActiveCompany.Id);
            Assert.Equal(secondEntityNumber, context.ActiveCompany.CompanyCode);
            Assert.Equal(2, context.AvailableCompanies.Count);
            var expectedCompanyCodes = new[] { firstEntityNumber, secondEntityNumber }
                .OrderBy(static code => code, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                expectedCompanyCodes,
                context.AvailableCompanies.Select(static company => company.CompanyCode).ToArray());
            Assert.Equal(["owner", "user"], context.User.Roles);
        }
        finally
        {
            await CleanupAsync(connectionFactory, userId, firstCompanyId, secondCompanyId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsNullWhenUserHasNoActiveMemberships()
    {
        var userId = UserId.FromOrdinal(1);
        var companyId = CompanyId.FromOrdinal(1);
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCompanySessionContextStore(connectionFactory);
        var workflow = new CompanySessionContextWorkflow(store);

        try
        {
            await SeedInactiveMembershipAsync(connectionFactory, userId, companyId, BuildEntityNumber(), CancellationToken.None);

            var context = await workflow.GetAsync(userId, companyId, CancellationToken.None);

            Assert.Null(context);
        }
        finally
        {
            await CleanupAsync(connectionFactory, userId, companyId, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetAsync_IncludesMembershipPermissionTokensInUserRoles()
    {
        var userId = UserId.FromOrdinal(1);
        var companyId = CompanyId.FromOrdinal(1);
        var entityNumber = BuildEntityNumber();
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlCompanySessionContextStore(connectionFactory);
        var workflow = new CompanySessionContextWorkflow(store);

        try
        {
            await using (var connection = await connectionFactory.OpenAsync(CancellationToken.None))
            {
                await InsertUserAsync(connection, userId, CancellationToken.None);
                await InsertCompanyAsync(connection, companyId, entityNumber, "Permission Session Co.", "USD", false, CancellationToken.None);
                await InsertMembershipAsync(
                    connection,
                    companyId,
                    userId,
                    "user",
                    true,
                    CancellationToken.None,
                    """["company_book_governance","ap"]""");
            }

            var context = await workflow.GetAsync(userId, companyId, CancellationToken.None);

            Assert.NotNull(context);
            Assert.Equal(["ap", "company_book_governance", "user"], context!.User.Roles);
        }
        finally
        {
            await CleanupAsync(connectionFactory, userId, companyId, null, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static string BuildEntityNumber()
    {
        var numeric = Math.Abs(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0)) % 100_000_000;
        return $"EN2099{numeric:D8}";
    }

    private static async Task SeedAsync(
        PostgreSqlConnectionFactory connectionFactory,
        UserId userId,
        CompanyId firstCompanyId,
        CompanyId secondCompanyId,
        string firstEntityNumber,
        string secondEntityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await InsertUserAsync(connection, userId, cancellationToken);
        await InsertCompanyAsync(connection, firstCompanyId, firstEntityNumber, "Northwind Session Ltd.", "USD", true, cancellationToken);
        await InsertCompanyAsync(connection, secondCompanyId, secondEntityNumber, "Blue Harbor Session Ltd.", "CAD", false, cancellationToken);
        await InsertMembershipAsync(connection, firstCompanyId, userId, "owner", true, cancellationToken);
        await InsertMembershipAsync(connection, secondCompanyId, userId, "user", true, cancellationToken);
    }

    private static async Task SeedInactiveMembershipAsync(
        PostgreSqlConnectionFactory connectionFactory,
        UserId userId,
        CompanyId companyId,
        string entityNumber,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await InsertUserAsync(connection, userId, cancellationToken);
        await InsertCompanyAsync(connection, companyId, entityNumber, "Inactive Membership Co.", "USD", false, cancellationToken);
        await InsertMembershipAsync(connection, companyId, userId, "user", false, cancellationToken);
    }

    private static async Task InsertUserAsync(
        Npgsql.NpgsqlConnection connection,
        UserId userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into users (id, email, username, password_hash, is_active)
            values (@id, @email, @username, @password_hash, true);
            """;
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", $"{userId:N}@example.test");
        command.Parameters.AddWithValue("username", "alice.session");
        command.Parameters.AddWithValue("password_hash", "hashed-password");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCompanyAsync(
        Npgsql.NpgsqlConnection connection,
        CompanyId companyId,
        string entityNumber,
        string legalName,
        string baseCurrencyCode,
        bool multiCurrencyEnabled,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into companies (
              id,
              entity_number,
              legal_name,
              base_currency_code,
              multi_currency_enabled,
              status
            )
            values (
              @id,
              @entity_number,
              @legal_name,
              @base_currency_code,
              @multi_currency_enabled,
              'active'
            );
            """;
        command.Parameters.AddWithValue("id", companyId);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("legal_name", legalName);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("multi_currency_enabled", multiCurrencyEnabled);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMembershipAsync(
        Npgsql.NpgsqlConnection connection,
        CompanyId companyId,
        UserId userId,
        string role,
        bool isActive,
        CancellationToken cancellationToken,
        string? permissionsJson = null)
    {
        var hasPermissionsColumn = await HasMembershipPermissionsColumnAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = hasPermissionsColumn
            ?
            """
            insert into company_memberships (
              company_id,
              user_id,
              role,
              permissions,
              is_active
            )
            values (
              @company_id,
              @user_id,
              @role,
              @permissions::jsonb,
              @is_active
            );
            """
            :
            """
            insert into company_memberships (
              company_id,
              user_id,
              role,
              is_active
            )
            values (
              @company_id,
              @user_id,
              @role,
              @is_active
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role", role);
        if (hasPermissionsColumn)
        {
            command.Parameters.AddWithValue("permissions", permissionsJson ?? "[]");
        }

        command.Parameters.AddWithValue("is_active", isActive);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasMembershipPermissionsColumnAsync(
        Npgsql.NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists (
              select 1
              from information_schema.columns
              where table_schema = 'public'
                and table_name = 'company_memberships'
                and column_name = 'permissions'
            );
            """;

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        UserId userId,
        CompanyId firstCompanyId,
        Guid? secondCompanyId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from company_memberships
            where user_id = @user_id;

            delete from companies
            where id = any(@company_ids);

            delete from users
            where id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        var companyIds = secondCompanyId.HasValue
            ? new[] { firstCompanyId, secondCompanyId.Value }
            : new[] { firstCompanyId };
        command.Parameters.AddWithValue("company_ids", companyIds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
