using Citus.Accounting.Application.Companies;
using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Companies;

public sealed class PostgreSqlCompanyMoneyDecimalsStore(PostgreSqlConnectionFactory connections)
    : ICompanyMoneyDecimalsStore
{
    public async Task SetAsync(CompanyId companyId, int moneyDecimals, CancellationToken cancellationToken)
    {
        if (moneyDecimals is not (2 or 3))
        {
            throw new InvalidOperationException("Money decimals must be 2 or 3.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update companies
               set money_decimals = @money_decimals,
                   updated_at = now()
             where id = @company_id;
            """;
        command.Parameters.AddWithValue("money_decimals", (short)moneyDecimals);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            throw new InvalidOperationException("The active company was not found.");
        }
    }

    public async Task<int> GetAsync(CompanyId companyId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select money_decimals from companies where id = @company_id;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        var decimals = raw is null or DBNull ? 2 : Convert.ToInt32(raw);
        return decimals is 2 or 3 ? decimals : 2;
    }
}
