using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Currencies;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresFxSnapshotRepository : IFxSnapshotRepository
{
    private readonly PostgresConnectionFactory _connections;
    private readonly PostgresExecutionContextAccessor _executionContextAccessor;

    public PostgresFxSnapshotRepository(
        PostgresConnectionFactory connections,
        PostgresExecutionContextAccessor executionContextAccessor)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
    }

    public async Task<FxSnapshotRef?> FindAcceptedSnapshotAsync(
        CompanyId companyId,
        CurrencyCode baseCurrencyCode,
        CurrencyCode quoteCurrencyCode,
        DateOnly requestedDate,
        Guid? snapshotId,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            _connections,
            _executionContextAccessor,
            cancellationToken);

        var sql = snapshotId is { } resolvedSnapshotId && resolvedSnapshotId != Guid.Empty
            ? """
              select
                s.id,
                s.base_currency_code,
                s.quote_currency_code,
                s.rate,
                s.requested_date,
                s.effective_date,
                s.snapshot_semantics
              from company_fx_rate_snapshots s
              where s.company_id = @company_id
                and s.id = @snapshot_id
                and s.base_currency_code = @base_currency_code
                and s.quote_currency_code = @quote_currency_code
              limit 1;
              """
            : """
              select
                s.id,
                s.base_currency_code,
                s.quote_currency_code,
                s.rate,
                s.requested_date,
                s.effective_date,
                s.snapshot_semantics
              from company_fx_rate_snapshots s
              where s.company_id = @company_id
                and s.base_currency_code = @base_currency_code
                and s.quote_currency_code = @quote_currency_code
                and s.requested_date <= @requested_date
                and s.effective_date <= @requested_date
              order by s.requested_date desc, s.effective_date desc, s.created_at desc
              limit 1;
              """;

        await using var command = scope.CreateCommand(sql);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode.Value);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode.Value);
        command.Parameters.AddWithValue("requested_date", requestedDate);

        if (snapshotId is { } explicitSnapshotId && explicitSnapshotId != Guid.Empty)
        {
            command.Parameters.AddWithValue("snapshot_id", explicitSnapshotId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FxSnapshotRef(
            reader.GetGuid(reader.GetOrdinal("id")),
            new CurrencyCode(reader.GetString(reader.GetOrdinal("base_currency_code"))),
            new CurrencyCode(reader.GetString(reader.GetOrdinal("quote_currency_code"))),
            reader.GetDecimal(reader.GetOrdinal("rate")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("requested_date")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_date")),
            reader.GetString(reader.GetOrdinal("snapshot_semantics")));
    }
}
