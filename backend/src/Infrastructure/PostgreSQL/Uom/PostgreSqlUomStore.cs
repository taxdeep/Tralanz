using Citus.Accounting.Application.Abstractions;

namespace Infrastructure.PostgreSQL.Uom;

/// <summary>
/// PostgreSQL implementation of <see cref="IUomStore"/>. Reads the
/// <c>units_of_measure</c> table installed by the
/// 2026-05-25-uom-foundation migration. The migration also seeds the 8
/// default UOMs for every existing company + installs an
/// <c>after-insert</c> trigger on <c>companies</c> so new companies
/// auto-seed, which means this store never has to provision anything —
/// it's a pure read surface today.
/// </summary>
public sealed class PostgreSqlUomStore(PostgreSqlConnectionFactory connections) : IUomStore
{
    private const string SelectColumns =
        "SELECT id, company_id, code, name, decimal_precision, category, is_active, created_at, updated_at FROM units_of_measure";

    public async Task<IReadOnlyList<UomRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var items = new List<UomRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? SelectColumns + " WHERE company_id = @company_id ORDER BY category NULLS LAST, code;"
            : SelectColumns + " WHERE company_id = @company_id AND is_active = TRUE ORDER BY category NULLS LAST, code;";
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new UomRecord(
                Id: reader.GetGuid(0),
                CompanyId: CompanyId.Parse(reader.GetString(1)),
                Code: reader.GetString(2),
                Name: reader.GetString(3),
                DecimalPrecision: reader.GetInt32(4),
                Category: reader.IsDBNull(5) ? null : reader.GetString(5),
                IsActive: reader.GetBoolean(6),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return items;
    }
}
