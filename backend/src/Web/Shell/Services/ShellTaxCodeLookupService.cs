using Infrastructure.PostgreSQL;

namespace Web.Shell.Services;

public sealed class ShellTaxCodeLookupService(PostgreSqlConnectionFactory connections)
{
    public Task<IReadOnlyList<ShellTaxCodeLookupOption>> ListSalesTaxCodesAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        ListAsync(companyId, "sales", cancellationToken);

    public Task<IReadOnlyList<ShellTaxCodeLookupOption>> ListPurchaseTaxCodesAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        ListAsync(companyId, "purchase", cancellationToken);

    private async Task<IReadOnlyList<ShellTaxCodeLookupOption>> ListAsync(
        Guid companyId,
        string appliesTo,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              code,
              name,
              rate_percent,
              applies_to,
              is_recoverable_on_purchase
            from tax_codes
            where company_id = @company_id
              and is_active = true
              and (applies_to = @applies_to or applies_to = 'both')
            order by code asc;
            """;
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("applies_to", appliesTo);

        var items = new List<ShellTaxCodeLookupOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShellTaxCodeLookupOption
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Code = reader.GetString(reader.GetOrdinal("code")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                RatePercent = reader.GetDecimal(reader.GetOrdinal("rate_percent")),
                AppliesTo = reader.GetString(reader.GetOrdinal("applies_to")),
                IsRecoverableOnPurchase = reader.GetBoolean(reader.GetOrdinal("is_recoverable_on_purchase"))
            });
        }

        return items;
    }
}

public sealed record class ShellTaxCodeLookupOption
{
    public Guid Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public decimal RatePercent { get; init; }

    public string AppliesTo { get; init; } = string.Empty;

    public bool IsRecoverableOnPurchase { get; init; }

    public string DisplayLabel => $"{Code} {Name} ({RatePercent:N2}%)";

    public string DefaultSummaryLabel => $"{Code} - {Name}";
}
