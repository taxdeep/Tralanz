using Citus.Modules.UnitySearch.Application.Contracts;
using NpgsqlTypes;

namespace Infrastructure.PostgreSQL.UnitySearch;

/// <summary>
/// PostgreSQL implementation of <see cref="ISearchDocumentEmbeddingStore"/>.
/// Both methods are tenant-scoped — the back-fill job for company A
/// cannot read or write any row owned by company B.
/// </summary>
public sealed class PostgreSqlSearchDocumentEmbeddingStore(PostgreSqlConnectionFactory connections)
    : ISearchDocumentEmbeddingStore
{
    public async Task<IReadOnlyList<SearchDocumentEmbeddingCandidate>> ListPendingAsync(
        CompanyId companyId,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var capped = Math.Clamp(batchSize, 1, 256);
        var list = new List<SearchDocumentEmbeddingCandidate>(capped);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // ix_search_documents_embedding_pending matches the predicate
        // shape, so this is an index range scan.
        command.CommandText =
            """
            select id, entity_type, primary_text, search_text
            from search_documents
            where company_id = @company_id
              and embedding is null
              and not is_voided
            order by id
            limit @batch_size;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("batch_size", capped);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new SearchDocumentEmbeddingCandidate(
                Id: reader.GetGuid(0),
                EntityType: reader.GetString(1),
                PrimaryText: reader.GetString(2),
                SearchText: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return list;
    }

    public async Task<int> UpdateEmbeddingsAsync(
        CompanyId companyId,
        IReadOnlyList<(Guid Id, string EmbeddingLiteral)> pairs,
        CancellationToken cancellationToken)
    {
        if (pairs.Count == 0) return 0;

        // Unnest two parallel arrays + cast the text literal back to
        // vector so we can do the whole batch in one round-trip.
        // company_id filter on the UPDATE prevents cross-tenant writes
        // even if the caller somehow passed a foreign id.
        var ids = pairs.Select(p => p.Id).ToArray();
        var literals = pairs.Select(p => p.EmbeddingLiteral).ToArray();

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            update search_documents d
            set embedding = u.literal::vector
            from unnest(@ids, @literals) as u(id, literal)
            where d.id = u.id
              and d.company_id = @company_id;
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        var idsParam = command.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        idsParam.Value = ids;
        var literalsParam = command.Parameters.Add("literals", NpgsqlDbType.Array | NpgsqlDbType.Text);
        literalsParam.Value = literals;

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
