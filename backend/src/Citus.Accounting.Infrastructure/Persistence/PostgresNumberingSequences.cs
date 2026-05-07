namespace Citus.Accounting.Infrastructure.Persistence;

internal static class PostgresNumberingSequences
{
    public static async Task<string> ReserveAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string scopeKey,
        string prefix,
        short padding,
        CancellationToken cancellationToken)
    {
        var entityYear = TryParseEntityNumberYear(scopeKey, prefix);
        if (entityYear.HasValue)
        {
            return await ReserveEntityNumberAsync(scope, companyId, entityYear.Value, prefix, padding, cancellationToken);
        }

        await using (var seedCommand = scope.CreateCommand(
                         """
                         insert into company_numbering_sequences (
                           company_id,
                           scope_key,
                           prefix,
                           next_number,
                           padding,
                           suggestion_enabled,
                           updated_at
                         )
                         values (
                           @company_id,
                           @scope_key,
                           @prefix,
                           1,
                           @padding,
                           true,
                           now()
                         )
                         on conflict (company_id, scope_key) do nothing;
                         """))
        {
            seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
            seedCommand.Parameters.AddWithValue("scope_key", scopeKey);
            seedCommand.Parameters.AddWithValue("prefix", prefix);
            seedCommand.Parameters.AddWithValue("padding", padding);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = scope.CreateCommand(
            """
            update company_numbering_sequences
            set next_number = next_number + 1,
                updated_at = now()
            where company_id = @company_id
              and scope_key = @scope_key
            returning prefix, next_number - 1 as issued_number, padding;
            """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("scope_key", scopeKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"No numbering sequence row was returned for scope '{scopeKey}'.");
        }

        var issuedPrefix = reader.GetString(reader.GetOrdinal("prefix"));
        var issuedNumber = reader.GetInt64(reader.GetOrdinal("issued_number"));
        var issuedPadding = reader.GetInt16(reader.GetOrdinal("padding"));

        return $"{issuedPrefix}{issuedNumber.ToString().PadLeft(issuedPadding, '0')}";
    }

    // Per-company entity-number reservation. The scope is (company_id,
    // entity_year), so each company independently advances its own
    // EN+YYYY+5base36 series starting at 1. Companies don't share a
    // namespace; collisions are scoped by company_id, which the
    // 2026-05-07 schema migration enforces with UNIQUE(company_id,
    // entity_number) on every audit-numbered table.
    private static async Task<string> ReserveEntityNumberAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        int year,
        string prefix,
        short padding,
        CancellationToken cancellationToken)
    {
        await EnsureCompanyEntityNumberSequenceAsync(scope, cancellationToken);

        var seedNumber = await FindEntitySeedNumberAsync(scope, companyId, year, cancellationToken);

        await using (var seedCommand = scope.CreateCommand(
            """
            insert into company_entity_number_sequences (company_id, entity_year, next_ordinal)
            values (@company_id, @entity_year, @seed_number)
            on conflict (company_id, entity_year) do update
              set next_ordinal = greatest(company_entity_number_sequences.next_ordinal, excluded.next_ordinal);
            """))
        {
            seedCommand.Parameters.AddWithValue("company_id", companyId.Value);
            seedCommand.Parameters.AddWithValue("entity_year", year);
            seedCommand.Parameters.AddWithValue("seed_number", seedNumber);
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = scope.CreateCommand(
            """
            update company_entity_number_sequences
            set next_ordinal = greatest(next_ordinal, @seed_number) + 1
            where company_id = @company_id and entity_year = @entity_year
            returning next_ordinal - 1 as issued_number;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_year", year);
        command.Parameters.AddWithValue("seed_number", seedNumber);

        var issuedNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? seedNumber);
        return $"{prefix}{Base36.Encode(issuedNumber, padding)}";
    }

    private static async Task EnsureCompanyEntityNumberSequenceAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            create table if not exists company_entity_number_sequences (
              company_id char(7) not null,
              entity_year integer not null,
              next_ordinal bigint not null,
              primary key (company_id, entity_year)
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Compute the next ordinal seed for (company, year) by scanning the
    // company's existing audit rows. The scan is filtered by company_id
    // so each company's sequence is independent and a row in another
    // company's books can't move this company's counter forward.
    //
    // The legacy decimal-only regex stays in place for backward compat:
    // entries written before the base36 cut-over are still pure digits
    // and need to participate in the seed. New base36 rows skip the
    // case branch (no match) and rely on next_ordinal in the sequence
    // table instead.
    private static async Task<long> FindEntitySeedNumberAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            $"""
            with all_entities as (
              select entity_number from manual_journal_documents where company_id = @company_id
              union all
              select entity_number from journal_entries where company_id = @company_id
              union all
              select entity_number from invoices where company_id = @company_id
              union all
              select entity_number from bills where company_id = @company_id
              union all
              select entity_number from credit_notes where company_id = @company_id
              union all
              select entity_number from vendor_credits where company_id = @company_id
              union all
              select entity_number from receive_payments where company_id = @company_id
              union all
              select entity_number from pay_bills where company_id = @company_id
              union all
              select entity_number from credit_applications where company_id = @company_id
              union all
              select entity_number from vendor_credit_applications where company_id = @company_id
              union all
              select entity_number from fx_revaluation_batches where company_id = @company_id
              union all
              select entity_number from accounts where company_id = @company_id
            )
            select coalesce(
              max(
                case
                  when entity_number ~ '^EN{year}[0-9]+$'
                    then substring(entity_number from 7)::bigint
                  else null
                end
              ),
              0
            ) + 1
            from all_entities;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 1L);
    }

    private static int? TryParseEntityNumberYear(string scopeKey, string prefix)
    {
        if (!scopeKey.StartsWith("entity-number:", StringComparison.Ordinal) ||
            prefix.Length != 6 ||
            !prefix.StartsWith("EN", StringComparison.Ordinal) ||
            !int.TryParse(prefix.AsSpan(2), out var year))
        {
            return null;
        }

        return year;
    }
}
