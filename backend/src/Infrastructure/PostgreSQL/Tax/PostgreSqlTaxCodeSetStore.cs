using Citus.Accounting.Application.Abstractions;

namespace Infrastructure.PostgreSQL.Tax;

/// <summary>
/// PostgreSQL implementation of <see cref="ITaxCodeSetStore"/>. Reads the
/// R1 <c>tax_code_sets</c> table + its <c>tax_code_set_rules</c> membership
/// (for the rule count). Company isolation is by the explicit
/// <c>company_id</c> filter; the connection's default OpenAsync bypasses
/// M13 RLS, matching the other sales_tax_* config readers.
/// </summary>
public sealed class PostgreSqlTaxCodeSetStore(PostgreSqlConnectionFactory connections) : ITaxCodeSetStore
{
    public async Task<IReadOnlyList<TaxCodeSetRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var items = new List<TaxCodeSetRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select
                s.id,
                s.code,
                s.name,
                s.applies_to,
                s.is_active,
                (select count(*) from tax_code_set_rules m where m.tax_code_set_id = s.id) as rule_count
            from tax_code_sets s
            where s.company_id = @company_id
            """
            + (includeInactive ? "" : "\n  and s.is_active = true")
            + "\norder by s.is_active desc, s.code;";
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new TaxCodeSetRecord(
                Id: reader.GetGuid(0),
                Code: reader.GetString(1),
                Name: reader.GetString(2),
                AppliesTo: reader.GetString(3),
                IsActive: reader.GetBoolean(4),
                RuleCount: (int)reader.GetInt64(5)));
        }
        return items;
    }
}
