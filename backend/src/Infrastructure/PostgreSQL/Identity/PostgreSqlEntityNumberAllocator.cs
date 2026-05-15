using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Identity;

public sealed class PostgreSqlEntityNumberAllocator : IEntityNumberAllocator
{
    public async Task<EntityNumber> AllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await using (var seedCommand = connection.CreateCommand())
        {
            seedCommand.Transaction = transaction;
            seedCommand.CommandText =
                """
                insert into company_entity_number_sequences (company_id, entity_year, next_ordinal)
                values (@company_id, @entity_year, 1)
                on conflict (company_id, entity_year) do nothing;
                """;
            seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
            seedCommand.Parameters.AddWithValue("entity_year", year);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            update company_entity_number_sequences
            set next_ordinal = next_ordinal + 1
            where company_id = @company_id and entity_year = @entity_year
            returning next_ordinal - 1 as ordinal;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_year", year);

        var ordinal = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return EntityNumber.Create(year, ordinal);
    }
}
