using System.Text.Json;
using Citus.Modules.UnitySearch.Application.Contracts;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.UnitySearch;

/// <summary>
/// PostgreSQL implementation of <see cref="IUnitysearchQueryIntentCacheStore"/>.
/// Reads sit on the search hot path; writes are off-path (backfill).
/// </summary>
public sealed class PostgreSqlUnitysearchQueryIntentCacheStore(PostgreSqlConnectionFactory connections)
    : IUnitysearchQueryIntentCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UnitysearchQueryIntent?> GetReadyAsync(
        CompanyId companyId,
        string queryHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryHash)) return null;

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Partial index ix_intent_cache_lookup matches the WHERE shape
        // (company_id, query_hash, status='ready') so this is a single
        // index lookup. expires_at check is a leaf eval.
        command.CommandText =
            """
            select entity_type_priors::text, expanded_terms, confidence
            from unitysearch_query_intent_cache
            where company_id = @company_id
              and query_hash = @query_hash
              and status = 'ready'
              and expires_at > now()
            limit 1;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("query_hash", queryHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var priorsJson = reader.GetString(0);
        var expandedTerms = reader.IsDBNull(1)
            ? Array.Empty<string>()
            : (string[])reader.GetValue(1);
        var confidence = reader.GetDecimal(2);

        var priors = JsonSerializer.Deserialize<Dictionary<string, decimal>>(priorsJson, JsonOptions)
                     ?? new Dictionary<string, decimal>();

        return new UnitysearchQueryIntent(priors, expandedTerms, confidence);
    }

    public async Task<bool> TryReservePendingAsync(
        CompanyId companyId,
        string queryHash,
        string normalizedQuery,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryHash)) return false;

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // ON CONFLICT DO NOTHING — if another worker already reserved
        // the slot (pending) or filled it (ready/failed), this returns
        // 0 affected rows and we step out of the way. RETURNING gives
        // us a row only if we actually inserted.
        command.CommandText =
            """
            insert into unitysearch_query_intent_cache
              (company_id, query_hash, normalized_query, status, source)
            values
              (@company_id, @query_hash, @normalized_query, 'pending', 'ai')
            on conflict on constraint uq_intent_cache_query do nothing
            returning id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("query_hash", queryHash);
        command.Parameters.AddWithValue("normalized_query", normalizedQuery);

        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        return inserted is not null;
    }

    public async Task MarkReadyAsync(
        CompanyId companyId,
        string queryHash,
        UnitysearchQueryIntent intent,
        string source,
        CancellationToken cancellationToken)
    {
        var priorsJson = JsonSerializer.Serialize(intent.EntityTypePriors, JsonOptions);
        var terms = intent.ExpandedTerms?.ToArray() ?? Array.Empty<string>();

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // UPDATE rather than UPSERT because TryReservePendingAsync always
        // runs first; the row is guaranteed to exist when we reach here.
        // If it somehow doesn't (race between cleanup and backfill), the
        // UPDATE affects 0 rows and we silently skip.
        command.CommandText =
            """
            update unitysearch_query_intent_cache
            set status = 'ready',
                entity_type_priors = @priors::jsonb,
                expanded_terms = @terms,
                confidence = @confidence,
                source = @source,
                failure_reason = null,
                updated_at = now(),
                -- Refresh TTL on every successful fill so frequently-asked
                -- queries don't accidentally expire mid-window.
                expires_at = now() + interval '14 days'
            where company_id = @company_id
              and query_hash = @query_hash;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("query_hash", queryHash);
        command.Parameters.AddWithValue("priors", priorsJson);
        var termsParam = command.Parameters.Add("terms", NpgsqlDbType.Array | NpgsqlDbType.Text);
        termsParam.Value = terms;
        command.Parameters.AddWithValue("confidence", intent.Confidence);
        command.Parameters.AddWithValue("source", string.IsNullOrWhiteSpace(source) ? "ai" : source);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        CompanyId companyId,
        string queryHash,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update unitysearch_query_intent_cache
            set status = 'failed',
                failure_reason = @reason,
                updated_at = now()
            where company_id = @company_id
              and query_hash = @query_hash;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("query_hash", queryHash);
        command.Parameters.AddWithValue("reason", reason ?? "unknown");

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
