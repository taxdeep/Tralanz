using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Tax;

/// <summary>
/// PostgreSQL implementation of <see cref="ITaxCodeSetStore"/>. Reads/writes
/// the R1 <c>tax_code_sets</c> + <c>tax_code_set_rules</c> tables. Member
/// rule details are joined from <c>tax_codes</c>. Company isolation is by the
/// explicit <c>company_id</c> filter; OpenAsync bypasses M13 RLS, matching
/// the other sales_tax_* config readers. Writes run in a single transaction.
/// </summary>
public sealed class PostgreSqlTaxCodeSetStore(PostgreSqlConnectionFactory connections) : ITaxCodeSetStore
{
    private const string SelectSetsWithMembers = """
        select
            s.id, s.code, s.name, s.applies_to, s.is_active,
            m.tax_rule_id, m.sequence, m.is_compound,
            tc.code as rule_code, tc.name as rule_name, tc.rate_percent
        from tax_code_sets s
        left join tax_code_set_rules m on m.tax_code_set_id = s.id
        left join tax_codes tc on tc.id = m.tax_rule_id
        """;

    public async Task<IReadOnlyList<TaxCodeSetRecord>> ListAsync(
        CompanyId companyId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSetsWithMembers
            + " where s.company_id = @company_id"
            + (includeInactive ? "" : " and s.is_active = true")
            + " order by s.is_active desc, s.code, m.sequence;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        return await ReadSetsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxCodeSetRecord?> GetByIdAsync(
        CompanyId companyId, Guid taxCodeSetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSetsWithMembers
            + " where s.company_id = @company_id and s.id = @id order by m.sequence;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", taxCodeSetId);
        var rows = await ReadSetsAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<TaxCodeSetRecord> CreateAsync(
        CompanyId companyId, TaxCodeSetUpsertInput input, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insertSet = connection.CreateCommand())
        {
            insertSet.Transaction = tx;
            insertSet.CommandText = """
                insert into tax_code_sets (id, company_id, code, name, applies_to, is_active)
                values (@id, @company_id, @code, @name, @applies_to, @is_active);
                """;
            insertSet.Parameters.AddWithValue("id", id);
            insertSet.Parameters.AddWithValue("company_id", companyId.Value);
            insertSet.Parameters.AddWithValue("code", input.Code.Trim());
            insertSet.Parameters.AddWithValue("name", input.Name.Trim());
            insertSet.Parameters.AddWithValue("applies_to", input.AppliesTo);
            insertSet.Parameters.AddWithValue("is_active", input.IsActive);
            await insertSet.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertMembersAsync(connection, tx, companyId, id, input.Members, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, id, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Tax code set insert returned no row.");
    }

    public async Task<TaxCodeSetRecord?> UpdateAsync(
        CompanyId companyId, Guid taxCodeSetId, TaxCodeSetUpsertInput input, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int updated;
        await using (var updateSet = connection.CreateCommand())
        {
            updateSet.Transaction = tx;
            updateSet.CommandText = """
                update tax_code_sets
                   set code = @code, name = @name, applies_to = @applies_to,
                       is_active = @is_active, updated_at = now()
                 where company_id = @company_id and id = @id;
                """;
            updateSet.Parameters.AddWithValue("id", taxCodeSetId);
            updateSet.Parameters.AddWithValue("company_id", companyId.Value);
            updateSet.Parameters.AddWithValue("code", input.Code.Trim());
            updateSet.Parameters.AddWithValue("name", input.Name.Trim());
            updateSet.Parameters.AddWithValue("applies_to", input.AppliesTo);
            updateSet.Parameters.AddWithValue("is_active", input.IsActive);
            updated = await updateSet.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Replace the membership wholesale — the set is small, so a diff
        // would be more code than value.
        await using (var deleteMembers = connection.CreateCommand())
        {
            deleteMembers.Transaction = tx;
            deleteMembers.CommandText = "delete from tax_code_set_rules where tax_code_set_id = @id;";
            deleteMembers.Parameters.AddWithValue("id", taxCodeSetId);
            await deleteMembers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertMembersAsync(connection, tx, companyId, taxCodeSetId, input.Members, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(companyId, taxCodeSetId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxCodeSetRecord?> SetActiveAsync(
        CompanyId companyId, Guid taxCodeSetId, bool isActive, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update tax_code_sets set is_active = @is_active, updated_at = now()
             where company_id = @company_id and id = @id;
            """;
        command.Parameters.AddWithValue("id", taxCodeSetId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("is_active", isActive);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rows == 0 ? null : await GetByIdAsync(companyId, taxCodeSetId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMembersAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CompanyId companyId, Guid setId,
        IReadOnlyList<TaxCodeSetMemberInput> members, CancellationToken cancellationToken)
    {
        var seq = 1;
        foreach (var member in members)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                insert into tax_code_set_rules (id, company_id, tax_code_set_id, tax_rule_id, sequence, is_compound)
                values (gen_random_uuid(), @company_id, @set_id, @rule_id, @sequence, @is_compound);
                """;
            cmd.Parameters.AddWithValue("company_id", companyId.Value);
            cmd.Parameters.AddWithValue("set_id", setId);
            cmd.Parameters.AddWithValue("rule_id", member.RuleId);
            cmd.Parameters.AddWithValue("sequence", member.Sequence > 0 ? member.Sequence : seq);
            cmd.Parameters.AddWithValue("is_compound", member.IsCompound);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            seq++;
        }
    }

    private static async Task<IReadOnlyList<TaxCodeSetRecord>> ReadSetsAsync(
        NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<Guid, (TaxCodeSetRecord Header, List<TaxCodeSetMemberRecord> Members)>();
        var order = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var setId = reader.GetGuid(0);
            if (!byId.TryGetValue(setId, out var entry))
            {
                entry = (new TaxCodeSetRecord(
                    Id: setId,
                    Code: reader.GetString(1),
                    Name: reader.GetString(2),
                    AppliesTo: reader.GetString(3),
                    IsActive: reader.GetBoolean(4),
                    Members: Array.Empty<TaxCodeSetMemberRecord>()),
                    new List<TaxCodeSetMemberRecord>());
                byId[setId] = entry;
                order.Add(setId);
            }
            // member columns are null for a set with no members (left join).
            if (!reader.IsDBNull(5))
            {
                entry.Members.Add(new TaxCodeSetMemberRecord(
                    RuleId: reader.GetGuid(5),
                    RuleCode: reader.GetString(8),
                    RuleName: reader.GetString(9),
                    RatePercent: reader.GetDecimal(10),
                    Sequence: reader.GetInt32(6),
                    IsCompound: reader.GetBoolean(7)));
            }
        }
        return order.Select(id =>
        {
            var (header, members) = byId[id];
            return header with { Members = members };
        }).ToList();
    }
}
