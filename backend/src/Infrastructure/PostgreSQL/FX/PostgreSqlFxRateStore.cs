using Engines.FX.FxRateLookup;
using Npgsql;
using NpgsqlTypes;
using SharedKernel.FX;

namespace Infrastructure.PostgreSQL.FX;

public sealed class PostgreSqlFxRateStore : IFxRateStore
{
    private readonly PostgreSqlConnectionFactory _connections;

    public PostgreSqlFxRateStore(PostgreSqlConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<FxSnapshotRecord?> FindLatestCompanySnapshotAsync(
        CompanyId companyId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        string providerKey,
        int lookbackDays,
        string rateType,
        string quoteBasis,
        string rateUseCase,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              created_at
            from company_fx_rate_snapshots
            where company_id = @company_id
              and base_currency_code = @base_currency_code
              and quote_currency_code = @quote_currency_code
              and rate_type = @rate_type
              and quote_basis = @quote_basis
              and rate_use_case = @rate_use_case
              and (snapshot_semantics = 'manual' or coalesce(provider_key, '') = @provider_key)
              and requested_date <= @requested_date
              and effective_date <= @requested_date
            order by requested_date desc, effective_date desc, created_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("rate_type", rateType);
        command.Parameters.AddWithValue("quote_basis", quoteBasis);
        command.Parameters.AddWithValue("rate_use_case", rateUseCase);
        command.Parameters.AddWithValue("provider_key", providerKey);
        command.Parameters.AddWithValue("requested_date", requestedDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var snapshot = ReadSnapshot(reader);
        var dayDelta = snapshot.RequestedDate.DayNumber - snapshot.EffectiveDate.DayNumber;
        return dayDelta <= lookbackDays ? snapshot : null;
    }

    public async Task<IReadOnlyList<FxSnapshotRecord>> ListCompanySnapshotsAsync(
        CompanyId companyId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              created_at
            from company_fx_rate_snapshots
            where company_id = @company_id
              and base_currency_code = @base_currency_code
              and quote_currency_code = @quote_currency_code
              and requested_date <= @requested_date
            order by requested_date desc, effective_date desc, created_at desc
            limit @take;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        command.Parameters.AddWithValue("take", take);

        var snapshots = new List<FxSnapshotRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(ReadSnapshot(reader));
        }

        return snapshots;
    }

    public async Task<FxMarketRateRecord?> FindLatestMarketRateAsync(
        string providerKey,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        int lookbackDays,
        string rateType,
        string quoteBasis,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              provider_key,
              base_currency_code,
              quote_currency_code,
              market_date,
              rate,
              rate_type,
              quote_basis,
              fetched_at,
              payload
            from system_fx_market_rates
            where provider_key = @provider_key
              and base_currency_code = @base_currency_code
              and quote_currency_code = @quote_currency_code
              and rate_type = @rate_type
              and quote_basis = @quote_basis
              and market_date <= @requested_date
            order by market_date desc, fetched_at desc
            limit 1;
            """;
        command.Parameters.AddWithValue("provider_key", providerKey);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("rate_type", rateType);
        command.Parameters.AddWithValue("quote_basis", quoteBasis);
        command.Parameters.AddWithValue("requested_date", requestedDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var marketRate = ReadMarketRate(reader);
        var dayDelta = requestedDate.DayNumber - marketRate.MarketDate.DayNumber;
        return dayDelta <= lookbackDays ? marketRate : null;
    }

    public async Task<IReadOnlyList<FxMarketRateRecord>> ListMarketRatesAsync(
        string providerKey,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              provider_key,
              base_currency_code,
              quote_currency_code,
              market_date,
              rate,
              rate_type,
              quote_basis,
              fetched_at,
              payload
            from system_fx_market_rates
            where provider_key = @provider_key
              and base_currency_code = @base_currency_code
              and quote_currency_code = @quote_currency_code
              and market_date <= @requested_date
            order by market_date desc, fetched_at desc
            limit @take;
            """;
        command.Parameters.AddWithValue("provider_key", providerKey);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        command.Parameters.AddWithValue("take", take);

        var marketRates = new List<FxMarketRateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            marketRates.Add(ReadMarketRate(reader));
        }

        return marketRates;
    }

    public async Task<FxSnapshotRecord?> FindCompanySnapshotByIdAsync(
        CompanyId companyId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              created_at
            from company_fx_rate_snapshots
            where company_id = @company_id
              and id = @id
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", snapshotId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadSnapshot(reader);
    }

    public async Task<FxMarketRateRecord?> FindMarketRateByIdAsync(
        Guid marketRateId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select
              id,
              provider_key,
              base_currency_code,
              quote_currency_code,
              market_date,
              rate,
              fetched_at,
              payload
            from system_fx_market_rates
            where id = @id
            limit 1;
            """;
        command.Parameters.AddWithValue("id", marketRateId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadMarketRate(reader);
    }

    public async Task<IReadOnlyList<FxMarketRateRecord>> UpsertMarketRatesAsync(
        IReadOnlyList<FxMarketRateRecord> marketRates,
        CancellationToken cancellationToken)
    {
        if (marketRates.Count == 0)
        {
            return [];
        }

        var storedRates = new List<FxMarketRateRecord>(marketRates.Count);

        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var marketRate in marketRates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into system_fx_market_rates (
                  id,
                  provider_key,
                  base_currency_code,
                  quote_currency_code,
                  market_date,
                  rate,
                  rate_type,
                  quote_basis,
                  fetched_at,
                  payload
                )
                values (
                  @id,
                  @provider_key,
                  @base_currency_code,
                  @quote_currency_code,
                  @market_date,
                  @rate,
                  @rate_type,
                  @quote_basis,
                  @fetched_at,
                  @payload
                )
                on conflict (provider_key, base_currency_code, quote_currency_code, market_date)
                do update
                  set rate = excluded.rate,
                      rate_type = excluded.rate_type,
                      quote_basis = excluded.quote_basis,
                      fetched_at = excluded.fetched_at,
                      payload = excluded.payload
                returning
                  id,
                  provider_key,
                  base_currency_code,
                  quote_currency_code,
                  market_date,
                  rate,
                  rate_type,
                  quote_basis,
                  fetched_at,
                  payload;
                """;
            command.Parameters.AddWithValue("id", marketRate.Id == Guid.Empty ? Guid.NewGuid() : marketRate.Id);
            command.Parameters.AddWithValue("provider_key", marketRate.ProviderKey);
            command.Parameters.AddWithValue("base_currency_code", marketRate.BaseCurrencyCode);
            command.Parameters.AddWithValue("quote_currency_code", marketRate.QuoteCurrencyCode);
            command.Parameters.AddWithValue("market_date", marketRate.MarketDate);
            command.Parameters.AddWithValue("rate", marketRate.Rate);
            command.Parameters.AddWithValue("rate_type", marketRate.RateType);
            command.Parameters.AddWithValue("quote_basis", marketRate.QuoteBasis);
            command.Parameters.AddWithValue("fetched_at", marketRate.FetchedAt);
            command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
                string.IsNullOrWhiteSpace(marketRate.PayloadJson) ? "{}" : marketRate.PayloadJson;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                storedRates.Add(ReadMarketRate(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return storedRates;
    }

    public async Task<FxSnapshotRecord> UpsertCompanySnapshotAsync(
        CompanyId companyId,
        UserId? createdByUserId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        FxMarketRateRecord marketRate,
        string providerKey,
        string rateType,
        string quoteBasis,
        string rateUseCase,
        string postingReason,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        Guid? existingSnapshotId = null;

        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.CommandText =
                """
                select id
                from company_fx_rate_snapshots
                where company_id = @company_id
                  and base_currency_code = @base_currency_code
                  and quote_currency_code = @quote_currency_code
                  and requested_date = @requested_date
                  and rate_type = @rate_type
                  and quote_basis = @quote_basis
                  and rate_use_case = @rate_use_case
                  and snapshot_semantics = 'system_stored'
                  and coalesce(provider_key, '') = @provider_key
                limit 1;
                """;
            existingCommand.Parameters.AddWithValue("company_id", companyId.Value);
            existingCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
            existingCommand.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
            existingCommand.Parameters.AddWithValue("requested_date", requestedDate);
            existingCommand.Parameters.AddWithValue("rate_type", rateType);
            existingCommand.Parameters.AddWithValue("quote_basis", quoteBasis);
            existingCommand.Parameters.AddWithValue("rate_use_case", rateUseCase);
            existingCommand.Parameters.AddWithValue("provider_key", providerKey);

            var existingValue = await existingCommand.ExecuteScalarAsync(cancellationToken);
            if (existingValue is Guid resolvedSnapshotId)
            {
                existingSnapshotId = resolvedSnapshotId;
            }
        }

        if (existingSnapshotId.HasValue)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText =
                """
                update company_fx_rate_snapshots
                set effective_date = @effective_date,
                    rate = @rate,
                    system_market_rate_id = @system_market_rate_id,
                    notes = @notes,
                    created_by_user_id = @created_by_user_id
                where id = @id
                returning
                  id,
                  company_id,
                  base_currency_code,
                  quote_currency_code,
                  requested_date,
                  effective_date,
                  rate,
                  rate_type,
                  quote_basis,
                  rate_use_case,
                  posting_reason,
                  provider_key,
                  row_origin,
                  snapshot_semantics,
                  system_market_rate_id,
                  created_at;
                """;
            updateCommand.Parameters.AddWithValue("id", existingSnapshotId.Value);
            updateCommand.Parameters.AddWithValue("effective_date", marketRate.MarketDate);
            updateCommand.Parameters.AddWithValue("rate", marketRate.Rate);
            updateCommand.Parameters.AddWithValue("system_market_rate_id", marketRate.Id);
            updateCommand.Parameters.AddWithValue("notes", $"Frankfurter v2 {providerKey}");
            updateCommand.Parameters.AddWithValue("created_by_user_id", createdByUserId.HasValue ? createdByUserId.Value : DBNull.Value);

            await using var reader = await updateCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return ReadSnapshot(reader);
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            insert into company_fx_rate_snapshots (
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              notes,
              created_by_user_id,
              created_at
            )
            values (
              @id,
              @company_id,
              @base_currency_code,
              @quote_currency_code,
              @requested_date,
              @effective_date,
              @rate,
              @rate_type,
              @quote_basis,
              @rate_use_case,
              @posting_reason,
              @provider_key,
              'provider_fetched',
              'system_stored',
              @system_market_rate_id,
              @notes,
              @created_by_user_id,
              now()
            )
            returning
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              created_at;
            """;
        insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
        insertCommand.Parameters.AddWithValue("company_id", companyId.Value);
        insertCommand.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        insertCommand.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        insertCommand.Parameters.AddWithValue("requested_date", requestedDate);
        insertCommand.Parameters.AddWithValue("effective_date", marketRate.MarketDate);
        insertCommand.Parameters.AddWithValue("rate", marketRate.Rate);
        insertCommand.Parameters.AddWithValue("rate_type", rateType);
        insertCommand.Parameters.AddWithValue("quote_basis", quoteBasis);
        insertCommand.Parameters.AddWithValue("rate_use_case", rateUseCase);
        insertCommand.Parameters.AddWithValue("posting_reason", postingReason);
        insertCommand.Parameters.AddWithValue("provider_key", providerKey);
        insertCommand.Parameters.AddWithValue("system_market_rate_id", marketRate.Id);
        insertCommand.Parameters.AddWithValue("notes", $"Frankfurter v2 {providerKey}");
        insertCommand.Parameters.AddWithValue("created_by_user_id", createdByUserId.HasValue ? createdByUserId.Value : DBNull.Value);

        await using var insertReader = await insertCommand.ExecuteReaderAsync(cancellationToken);
        await insertReader.ReadAsync(cancellationToken);
        return ReadSnapshot(insertReader);
    }

    public async Task<FxSnapshotRecord> CreateManualCompanySnapshotAsync(
        CompanyId companyId,
        UserId? createdByUserId,
        string baseCurrencyCode,
        string quoteCurrencyCode,
        DateOnly requestedDate,
        decimal rate,
        string providerKey,
        string rateType,
        string quoteBasis,
        string rateUseCase,
        string postingReason,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into company_fx_rate_snapshots (
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              notes,
              created_by_user_id,
              created_at
            )
            values (
              @id,
              @company_id,
              @base_currency_code,
              @quote_currency_code,
              @requested_date,
              @effective_date,
              @rate,
              @rate_type,
              @quote_basis,
              @rate_use_case,
              @posting_reason,
              @provider_key,
              'manual',
              'manual',
              null,
              @notes,
              @created_by_user_id,
              now()
            )
            returning
              id,
              company_id,
              base_currency_code,
              quote_currency_code,
              requested_date,
              effective_date,
              rate,
              rate_type,
              quote_basis,
              rate_use_case,
              posting_reason,
              provider_key,
              row_origin,
              snapshot_semantics,
              system_market_rate_id,
              created_at;
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("base_currency_code", baseCurrencyCode);
        command.Parameters.AddWithValue("quote_currency_code", quoteCurrencyCode);
        command.Parameters.AddWithValue("requested_date", requestedDate);
        command.Parameters.AddWithValue("effective_date", requestedDate);
        command.Parameters.AddWithValue("rate", rate);
        command.Parameters.AddWithValue("rate_type", rateType);
        command.Parameters.AddWithValue("quote_basis", quoteBasis);
        command.Parameters.AddWithValue("rate_use_case", rateUseCase);
        command.Parameters.AddWithValue("posting_reason", postingReason);
        command.Parameters.AddWithValue("provider_key", providerKey);
        command.Parameters.AddWithValue("notes", "Manual JE FX override");
        command.Parameters.Add(new NpgsqlParameter<Guid?>("created_by_user_id", NpgsqlDbType.Uuid)
        {
            TypedValue = createdByUserId
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadSnapshot(reader);
    }

    private static FxSnapshotRecord ReadSnapshot(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("quote_currency_code")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("requested_date")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("effective_date")),
            reader.GetDecimal(reader.GetOrdinal("rate")),
            reader.GetString(reader.GetOrdinal("rate_type")),
            reader.GetString(reader.GetOrdinal("quote_basis")),
            reader.GetString(reader.GetOrdinal("rate_use_case")),
            reader.GetString(reader.GetOrdinal("posting_reason")),
            reader.IsDBNull(reader.GetOrdinal("provider_key")) ? null : reader.GetString(reader.GetOrdinal("provider_key")),
            reader.GetString(reader.GetOrdinal("row_origin")),
            reader.GetString(reader.GetOrdinal("snapshot_semantics")),
            reader.IsDBNull(reader.GetOrdinal("system_market_rate_id")) ? null : reader.GetGuid(reader.GetOrdinal("system_market_rate_id")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")));

    private static FxMarketRateRecord ReadMarketRate(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("provider_key")),
            reader.GetString(reader.GetOrdinal("base_currency_code")),
            reader.GetString(reader.GetOrdinal("quote_currency_code")),
            reader.GetFieldValue<DateOnly>(reader.GetOrdinal("market_date")),
            reader.GetDecimal(reader.GetOrdinal("rate")),
            reader.GetString(reader.GetOrdinal("rate_type")),
            reader.GetString(reader.GetOrdinal("quote_basis")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("fetched_at")),
            reader.IsDBNull(reader.GetOrdinal("payload")) ? null : reader.GetString(reader.GetOrdinal("payload")));
}
