// PostgreSqlSalesTaxCatalogReader — implements ISalesTaxCatalogReader.
//
// Single SQL query joins sales_tax_codes ↔ components ↔ as-of rate ↔
// jurisdiction ↔ aggregated box codes for the requested
// legacy_tax_code_ids. Designed for engine hot-path: each
// document save runs this once per save.

using System.Data;
using Citus.Modules.SalesTax.Application.Contracts;
using Npgsql;

namespace Infrastructure.PostgreSQL.SalesTax;

public sealed class PostgreSqlSalesTaxCatalogReader : ISalesTaxCatalogReader
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlSalesTaxCatalogReader(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TaxCatalogComponentRow>>> GetComponentsForLegacyIdsAsync(
        string companyId,
        IReadOnlyList<Guid> legacyTaxCodeIds,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        if (legacyTaxCodeIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<TaxCatalogComponentRow>>();
        }

        const string sql = """
            select
                stc.legacy_tax_code_id   as legacy_id,
                stc.id                   as tax_code_id,
                stc.code                 as code,
                stc.name                 as name,
                stc.treatment            as treatment,
                c.id                     as component_id,
                c.jurisdiction_id        as jurisdiction_id,
                j.regime_type            as regime_type,
                c.sequence               as sequence,
                c.is_compound            as is_compound,
                c.recoverability_mode    as recoverability_mode,
                c.recoverable_percent    as recoverable_percent,
                c.payable_account_id        as payable_account_id,
                c.recoverable_account_id    as recoverable_account_id,
                c.non_recoverable_account_id as non_recoverable_account_id,
                coalesce(r.rate_percent, 0) as rate_percent,
                coalesce(
                    (select array_agg(distinct b.box_code order by b.box_code)
                     from sales_tax_code_component_box_mappings m
                     join sales_tax_reporting_boxes b on b.id = m.box_id
                     where m.component_id = c.id),
                    '{}'::text[]
                ) as box_codes
            from sales_tax_codes stc
            join sales_tax_code_components c on c.tax_code_id = stc.id
            join sales_tax_jurisdictions j   on j.id = c.jurisdiction_id
            left join lateral (
                select rr.rate_percent
                  from sales_tax_code_component_rates rr
                 where rr.component_id = c.id
                   and rr.effective_from <= @as_of
                   and (rr.effective_to is null or rr.effective_to > @as_of)
                 order by rr.effective_from desc
                 limit 1
            ) r on true
            where stc.company_id = @company_id
              and stc.legacy_tax_code_id is not null
              and stc.legacy_tax_code_id = any(@legacy_ids)
              and stc.is_active = true
            order by stc.legacy_tax_code_id, c.sequence;
            """;

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("legacy_ids", legacyTaxCodeIds.ToArray());
        command.Parameters.AddWithValue("as_of", asOfDate);

        var byLegacy = new Dictionary<Guid, List<TaxCatalogComponentRow>>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var legacyId = reader.GetGuid(reader.GetOrdinal("legacy_id"));
            if (!byLegacy.TryGetValue(legacyId, out var list))
            {
                list = new List<TaxCatalogComponentRow>(1);
                byLegacy[legacyId] = list;
            }
            list.Add(new TaxCatalogComponentRow(
                TaxCodeId: reader.GetGuid(reader.GetOrdinal("tax_code_id")),
                Code: reader.GetString(reader.GetOrdinal("code")),
                Name: reader.GetString(reader.GetOrdinal("name")),
                Treatment: reader.GetString(reader.GetOrdinal("treatment")),
                ComponentId: reader.GetGuid(reader.GetOrdinal("component_id")),
                JurisdictionId: reader.GetGuid(reader.GetOrdinal("jurisdiction_id")),
                RegimeType: reader.GetString(reader.GetOrdinal("regime_type")),
                Sequence: reader.GetInt32(reader.GetOrdinal("sequence")),
                IsCompound: reader.GetBoolean(reader.GetOrdinal("is_compound")),
                RecoverabilityMode: reader.GetString(reader.GetOrdinal("recoverability_mode")),
                RecoverablePercent: reader.IsDBNull(reader.GetOrdinal("recoverable_percent"))
                    ? null
                    : reader.GetDecimal(reader.GetOrdinal("recoverable_percent")),
                RatePercent: reader.GetDecimal(reader.GetOrdinal("rate_percent")),
                BoxCodes: (string[])reader.GetValue(reader.GetOrdinal("box_codes")),
                PayableAccountId: reader.IsDBNull(reader.GetOrdinal("payable_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("payable_account_id")),
                RecoverableAccountId: reader.IsDBNull(reader.GetOrdinal("recoverable_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("recoverable_account_id")),
                NonRecoverableAccountId: reader.IsDBNull(reader.GetOrdinal("non_recoverable_account_id"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("non_recoverable_account_id"))));
        }

        return byLegacy.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<TaxCatalogComponentRow>)kv.Value);
    }
}
