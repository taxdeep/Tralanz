using Citus.Accounting.Application.Companies;
using Citus.Accounting.Infrastructure.Persistence;

namespace Citus.Accounting.Infrastructure.Companies;

public sealed class PostgresCompanyProfileQuery : ICompanyProfileQuery
{
    private readonly PostgresConnectionFactory _connections;

    public PostgresCompanyProfileQuery(PostgresConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<CompanyProfileSnapshot?> GetByIdAsync(CompanyId companyId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, entity_number, legal_name, email, phone, address_line, city,
                   province_state, postal_code, country, base_currency_code
              from companies
             where id = @id
            """;

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompanyProfileSnapshot(
            Id: CompanyId.Parse(reader.GetString(0)),
            EntityNumber: reader.GetString(1),
            LegalName: reader.GetString(2),
            Email: reader.IsDBNull(3) ? null : reader.GetString(3),
            Phone: reader.IsDBNull(4) ? null : reader.GetString(4),
            AddressLine: reader.IsDBNull(5) ? null : reader.GetString(5),
            City: reader.IsDBNull(6) ? null : reader.GetString(6),
            ProvinceState: reader.IsDBNull(7) ? null : reader.GetString(7),
            PostalCode: reader.IsDBNull(8) ? null : reader.GetString(8),
            Country: reader.IsDBNull(9) ? null : reader.GetString(9),
            BaseCurrencyCode: reader.GetString(10));
    }
}
