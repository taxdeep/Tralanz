using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.CompanyAccess;
using Modules.CompanyAccess.SystemSetup;
using Npgsql;
using SharedKernel.CompanyAccess;

namespace Tests.CompanyAccess;

public sealed class SystemSetupSmokeTests
{
    private static readonly UserId DemoUserId = UserId.FromOrdinal(1);

    [Fact]
    public async Task SaveNumberDisplayModeAsync_PersistsUserPreference()
    {
        var connectionFactory = new PostgreSqlConnectionFactory(GetConnectionString());
        var store = new PostgreSqlSystemSetupStore(connectionFactory);
        var workflow = new SystemSetupWorkflow(store);

        try
        {
            var saved = await workflow.SaveNumberDisplayModeAsync(DemoUserId, "space-comma", CancellationToken.None);
            Assert.Equal(NumberDisplayMode.SpaceComma, saved.NumberDisplayMode);

            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                select number_display_mode
                from user_preferences
                where user_id = @user_id;
                """;
            command.Parameters.AddWithValue("user_id", DemoUserId.Value);

            var storedMode = Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None));
            Assert.Equal("space-comma", storedMode);
        }
        finally
        {
            await CleanupAsync(connectionFactory, CancellationToken.None);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable("CITUS_ACCOUNTING_DB")
        ?? "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me";

    private static async Task CleanupAsync(
        PostgreSqlConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from user_preferences
            where user_id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", DemoUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
