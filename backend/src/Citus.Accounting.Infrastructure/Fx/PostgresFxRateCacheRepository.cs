using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Infrastructure.Persistence;
using Npgsql;

namespace Citus.Accounting.Infrastructure.Fx;

/// <summary>
/// Postgres-backed cache for ECB / frankfurter FX rates. The table is
/// global (no company_id column) because the underlying market rate
/// is identical for every tenant. Rows are uniquely keyed by
/// (rate_date, base_code, quote_code) so the same cache can serve
/// USD→CAD lookups whether the asker has USD-base or CAD-base.
/// </summary>
public sealed class PostgresFxRateCacheRepository : IFxRateCacheRepository
{
    private readonly PostgresConnectionFactory _connectionFactory;

    public PostgresFxRateCacheRepository(PostgresConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists fx_rates_daily (
                id              uuid primary key default gen_random_uuid(),
                rate_date       date not null,
                base_code       char(3) not null,
                quote_code      char(3) not null,
                rate            numeric(20, 10) not null,
                source          text not null,
                fetched_at      timestamptz not null default now(),
                constraint fx_rates_daily_unique unique (rate_date, base_code, quote_code)
            );
            create index if not exists idx_fx_rates_daily_pair_date
                on fx_rates_daily (base_code, quote_code, rate_date desc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FxRateCacheRow?> GetAsync(
        DateOnly rateDate,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select rate_date, base_code, quote_code, rate, source
            from fx_rates_daily
            where rate_date = @rate_date
              and base_code = @base_code
              and quote_code = @quote_code
            limit 1;
            """;
        command.Parameters.AddWithValue("rate_date", rateDate);
        command.Parameters.AddWithValue("base_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_code", quoteCurrencyCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadRow(reader);
    }

    public async Task<FxRateCacheRow?> GetLatestBeforeAsync(
        DateOnly upperBoundDate,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        int maxLookbackDays,
        CancellationToken cancellationToken)
    {
        if (maxLookbackDays <= 0)
        {
            return null;
        }
        var floor = upperBoundDate.AddDays(-maxLookbackDays);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select rate_date, base_code, quote_code, rate, source
            from fx_rates_daily
            where base_code = @base_code
              and quote_code = @quote_code
              and rate_date <= @upper_bound
              and rate_date >= @floor
            order by rate_date desc
            limit 1;
            """;
        command.Parameters.AddWithValue("base_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("upper_bound", upperBoundDate);
        command.Parameters.AddWithValue("floor", floor);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadRow(reader);
    }

    public async Task UpsertManyAsync(
        IReadOnlyList<FxRateCacheRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                insert into fx_rates_daily (rate_date, base_code, quote_code, rate, source, fetched_at)
                values (@rate_date, @base_code, @quote_code, @rate, @source, now())
                on conflict (rate_date, base_code, quote_code)
                do update
                  set rate = excluded.rate,
                      source = excluded.source,
                      fetched_at = excluded.fetched_at;
                """;
            command.Parameters.AddWithValue("rate_date", row.RateDate);
            command.Parameters.AddWithValue("base_code", row.BaseCurrencyCode);
            command.Parameters.AddWithValue("quote_code", row.QuoteCurrencyCode);
            command.Parameters.AddWithValue("rate", row.Rate);
            command.Parameters.AddWithValue("source", row.Source);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FxRateCacheRow ReadRow(NpgsqlDataReader reader) =>
        new(
            DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("rate_date"))),
            reader.GetString(reader.GetOrdinal("base_code")),
            reader.GetString(reader.GetOrdinal("quote_code")),
            reader.GetDecimal(reader.GetOrdinal("rate")),
            reader.GetString(reader.GetOrdinal("source")));
}
