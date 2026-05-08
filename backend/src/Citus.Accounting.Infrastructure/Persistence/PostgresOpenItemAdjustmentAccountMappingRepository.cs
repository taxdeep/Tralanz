using System.Text.Json;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace Citus.Accounting.Infrastructure.Persistence;

public sealed class PostgresOpenItemAdjustmentAccountMappingRepository(
    PostgresConnectionFactory connections,
    PostgresExecutionContextAccessor executionContextAccessor) : IOpenItemAdjustmentAccountMappingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedOpenItemTypes = new(StringComparer.Ordinal)
    {
        "ar_open_item",
        "ap_open_item"
    };

    private static readonly HashSet<string> AllowedAdjustmentTypes = new(StringComparer.Ordinal)
    {
        "write_off",
        "small_balance_adjustment"
    };

    private static readonly HashSet<string> AllowedPolicyScopes = new(StringComparer.Ordinal)
    {
        "company_default",
        "book_specific",
        "primary_execution",
        "governance_only"
    };

    // Stage-1.4: cache + information_schema probe (same pattern as
    // commit 2ef2640) so EnsureSchemaAsync stops taking
    // AccessExclusiveLock on the mapping table after the first call.
    private static volatile bool _schemaEnsured;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await using var probeScope = await PostgresCommandScope.CreateAsync(
            connections,
            executionContextAccessor,
            cancellationToken);
        await using (var probe = probeScope.CreateCommand(
            """
            select count(*)
            from information_schema.columns
            where table_schema = 'public'
              and table_name = 'open_item_adjustment_account_mappings'
              and column_name = 'deactivated_at';
            """))
        {
            var present = Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
            if (present == 1)
            {
                _schemaEnsured = true;
                return;
            }
        }

        const string sql = """
            create table if not exists open_item_adjustment_account_mappings (
              id uuid primary key,
              company_id char(7) not null,
              book_id uuid null,
              open_item_type text not null,
              adjustment_type text not null,
              adjustment_account_id uuid not null,
              is_active boolean not null default true,
              created_by_user_id char(7) null,
              updated_by_user_id char(7) null,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              deactivated_at timestamptz null
            );

            alter table open_item_adjustment_account_mappings
              add column if not exists book_id uuid null,
              add column if not exists created_by_user_id char(7) null,
              add column if not exists updated_by_user_id char(7) null,
              add column if not exists deactivated_at timestamptz null;

            do $$
            begin
              if not exists (
                select 1
                from pg_constraint
                where conname = 'ck_open_item_adjustment_mapping_open_item_type'
              ) then
                alter table open_item_adjustment_account_mappings
                  add constraint ck_open_item_adjustment_mapping_open_item_type
                  check (lower(open_item_type) in ('ar_open_item', 'ap_open_item'));
              end if;

              if not exists (
                select 1
                from pg_constraint
                where conname = 'ck_open_item_adjustment_mapping_adjustment_type'
              ) then
                alter table open_item_adjustment_account_mappings
                  add constraint ck_open_item_adjustment_mapping_adjustment_type
                  check (lower(adjustment_type) in ('write_off', 'small_balance_adjustment'));
              end if;
            end $$;

            create index if not exists ix_open_item_adjustment_account_mappings_company_lookup
              on open_item_adjustment_account_mappings (
                company_id,
                open_item_type,
                adjustment_type,
                book_id,
                is_active
              );

            create unique index if not exists ux_open_item_adjustment_account_mappings_active_scope
              on open_item_adjustment_account_mappings (
                company_id,
                lower(open_item_type),
                lower(adjustment_type),
                coalesce(book_id, '00000000-0000-0000-0000-000000000000'::uuid)
              )
              where is_active = true;
            """;

        await using var scope = await PostgresCommandScope.CreateAsync(
            connections,
            executionContextAccessor,
            cancellationToken);
        await using var command = scope.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaEnsured = true;
    }

    public async Task<OpenItemAdjustmentAccountMappingLookupResult> LookupAsync(
        OpenItemAdjustmentAccountMappingLookupRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = await PostgresCommandScope.CreateAsync(
            connections,
            executionContextAccessor,
            cancellationToken);
        var primaryBookId = await TryGetPrimaryBookIdAsync(scope, request.CompanyId, cancellationToken);
        var allMappings = await ListCoreAsync(
            scope,
            request.CompanyId,
            request.OpenItemType,
            request.AdjustmentType,
            request.IncludeInactive,
            cancellationToken);

        var summary = new OpenItemAdjustmentAccountMappingLookupSummary(
            allMappings.Count,
            0,
            0,
            allMappings.Count(static mapping => mapping.IsActive),
            allMappings.Count(static mapping => mapping.IsActive && mapping.BookId is null),
            allMappings.Count(static mapping => mapping.IsActive && mapping.BookId is not null),
            allMappings.Count(static mapping => !mapping.IsActive));

        var filtered = allMappings
            .Where(mapping => MatchesBookFilter(mapping, request.BookId))
            .Where(mapping => MatchesPolicyScope(mapping, request.PolicyScope, primaryBookId))
            .Where(mapping => MatchesSearch(mapping, request.SearchText))
            .ToArray();

        var limited = filtered
            .Take(Math.Clamp(request.Limit <= 0 ? 200 : request.Limit, 1, 500))
            .ToArray();

        return new OpenItemAdjustmentAccountMappingLookupResult(
            summary with
            {
                VisibleMappings = filtered.Length,
                ReturnedMappings = limited.Length
            },
            limited);
    }

    private async Task<IReadOnlyList<OpenItemAdjustmentAccountMappingRecord>> ListCoreAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        string? openItemType,
        string? adjustmentType,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        var normalizedOpenItemType = string.IsNullOrWhiteSpace(openItemType)
            ? null
            : NormalizeOpenItemType(openItemType);
        var normalizedAdjustmentType = string.IsNullOrWhiteSpace(adjustmentType)
            ? null
            : NormalizeAdjustmentType(adjustmentType);
        var companyBooksTableExists = await CompanyBooksTableExistsAsync(scope, cancellationToken);

        await using var command = scope.CreateCommand(
            companyBooksTableExists
                ? """
                  select
                    m.id,
                    m.company_id,
                    m.book_id,
                    b.book_code,
                    b.accounting_standard,
                    lower(m.open_item_type) as open_item_type,
                    lower(m.adjustment_type) as adjustment_type,
                    m.adjustment_account_id,
                    a.code as adjustment_account_code,
                    a.name as adjustment_account_name,
                    a.root_type as adjustment_account_root_type,
                    m.is_active,
                    m.created_by_user_id,
                    m.updated_by_user_id,
                    m.created_at,
                    m.updated_at,
                    m.deactivated_at
                  from open_item_adjustment_account_mappings m
                  join accounts a
                    on a.company_id = m.company_id
                   and a.id = m.adjustment_account_id
                  left join company_books b
                    on b.company_id = m.company_id
                   and b.id = m.book_id
                  where m.company_id = @company_id
                    and (@open_item_type is null or lower(m.open_item_type) = @open_item_type)
                    and (@adjustment_type is null or lower(m.adjustment_type) = @adjustment_type)
                    and (@include_inactive = true or m.is_active = true)
                  order by
                    m.is_active desc,
                    lower(m.open_item_type),
                    lower(m.adjustment_type),
                    b.book_code nulls first,
                    m.updated_at desc;
                  """
                : """
                  select
                    m.id,
                    m.company_id,
                    m.book_id,
                    null::text as book_code,
                    null::text as accounting_standard,
                    lower(m.open_item_type) as open_item_type,
                    lower(m.adjustment_type) as adjustment_type,
                    m.adjustment_account_id,
                    a.code as adjustment_account_code,
                    a.name as adjustment_account_name,
                    a.root_type as adjustment_account_root_type,
                    m.is_active,
                    m.created_by_user_id,
                    m.updated_by_user_id,
                    m.created_at,
                    m.updated_at,
                    m.deactivated_at
                  from open_item_adjustment_account_mappings m
                  join accounts a
                    on a.company_id = m.company_id
                   and a.id = m.adjustment_account_id
                  where m.company_id = @company_id
                    and (@open_item_type is null or lower(m.open_item_type) = @open_item_type)
                    and (@adjustment_type is null or lower(m.adjustment_type) = @adjustment_type)
                    and (@include_inactive = true or m.is_active = true)
                  order by
                    m.is_active desc,
                    lower(m.open_item_type),
                    lower(m.adjustment_type),
                    m.updated_at desc;
                  """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.Add("open_item_type", NpgsqlDbType.Text).Value =
            normalizedOpenItemType is null ? DBNull.Value : normalizedOpenItemType;
        command.Parameters.Add("adjustment_type", NpgsqlDbType.Text).Value =
            normalizedAdjustmentType is null ? DBNull.Value : normalizedAdjustmentType;
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        var results = new List<OpenItemAdjustmentAccountMappingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadMapping(reader));
        }

        return results;
    }

    public async Task<OpenItemAdjustmentAccountMappingSaveResult> SaveAsync(
        OpenItemAdjustmentAccountMappingSaveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        var openItemType = NormalizeOpenItemType(request.OpenItemType);
        var adjustmentType = NormalizeAdjustmentType(request.AdjustmentType);

        await using var scope = await PostgresCommandScope.CreateAsync(
            connections,
            executionContextAccessor,
            cancellationToken);

        if (request.BookId.HasValue)
        {
            await EnsureBookBelongsToCompanyAsync(
                scope,
                request.CompanyId,
                request.BookId.Value,
                cancellationToken);
        }

        await EnsureAdjustmentAccountIsAllowedAsync(
            scope,
            request.CompanyId,
            request.AdjustmentAccountId,
            cancellationToken);

        var mappingId = Guid.NewGuid();
        await using (var deactivateCommand = scope.CreateCommand(
                         """
                         update open_item_adjustment_account_mappings existing
                         set is_active = false,
                             updated_by_user_id = @actor_id,
                             updated_at = now(),
                             deactivated_at = now()
                         where existing.company_id = @company_id
                           and lower(existing.open_item_type) = @open_item_type
                           and lower(existing.adjustment_type) = @adjustment_type
                           and coalesce(existing.book_id, '00000000-0000-0000-0000-000000000000'::uuid)
                               = coalesce(@book_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                           and existing.is_active = true;
                         """))
        {
            deactivateCommand.Parameters.AddWithValue("company_id", request.CompanyId.Value);
            deactivateCommand.Parameters.AddWithValue("book_id", request.BookId.HasValue ? request.BookId.Value : DBNull.Value);
            deactivateCommand.Parameters.AddWithValue("open_item_type", openItemType);
            deactivateCommand.Parameters.AddWithValue("adjustment_type", adjustmentType);
            deactivateCommand.Parameters.AddWithValue("actor_id", request.ActorId.HasValue ? (object)request.ActorId.Value.Value : DBNull.Value);
            await deactivateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = scope.CreateCommand(
            """
            with inserted as (
              insert into open_item_adjustment_account_mappings (
                id,
                company_id,
                book_id,
                open_item_type,
                adjustment_type,
                adjustment_account_id,
                is_active,
                created_by_user_id,
                updated_by_user_id,
                created_at,
                updated_at
              )
              values (
                @mapping_id,
                @company_id,
                @book_id,
                @open_item_type,
                @adjustment_type,
                @adjustment_account_id,
                true,
                @actor_id,
                @actor_id,
                now(),
                now()
              )
              returning *
            )
            select
              i.id,
              i.company_id,
              i.book_id,
              null::text as book_code,
              null::text as accounting_standard,
              lower(i.open_item_type) as open_item_type,
              lower(i.adjustment_type) as adjustment_type,
              i.adjustment_account_id,
              a.code as adjustment_account_code,
              a.name as adjustment_account_name,
              a.root_type as adjustment_account_root_type,
              i.is_active,
              i.created_by_user_id,
              i.updated_by_user_id,
              i.created_at,
              i.updated_at,
              i.deactivated_at
            from inserted i
            join accounts a
              on a.company_id = i.company_id
             and a.id = i.adjustment_account_id;
            """);

        command.Parameters.AddWithValue("mapping_id", mappingId);
        command.Parameters.AddWithValue("company_id", request.CompanyId.Value);
        command.Parameters.AddWithValue("book_id", request.BookId.HasValue ? request.BookId.Value : DBNull.Value);
        command.Parameters.AddWithValue("open_item_type", openItemType);
        command.Parameters.AddWithValue("adjustment_type", adjustmentType);
        command.Parameters.AddWithValue("adjustment_account_id", request.AdjustmentAccountId);
        command.Parameters.AddWithValue("actor_id", request.ActorId.HasValue ? (object)request.ActorId.Value.Value : DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Open-item adjustment account mapping could not be saved.");
        }

        var mapping = ReadMapping(reader);
        await reader.DisposeAsync();

        await InsertAuditLogIfAvailableAsync(
            scope,
            mapping.CompanyId,
            mapping.MappingId,
            request.ActorId,
            "open_item_adjustment_account_mapping_saved",
            new
            {
                mapping.MappingId,
                CompanyId = mapping.CompanyId,
                mapping.BookId,
                mapping.OpenItemType,
                mapping.AdjustmentType,
                mapping.AdjustmentAccountId,
                mapping.AdjustmentAccountCode,
                mapping.AdjustmentAccountRootType
            },
            cancellationToken);

        return new OpenItemAdjustmentAccountMappingSaveResult(
            mapping,
            "mapping_saved",
            "Open-item adjustment account mapping was saved and activated for the company/book policy scope.");
    }

    public async Task<OpenItemAdjustmentAccountMappingTransitionResult?> DeactivateAsync(
        CompanyId companyId,
        Guid mappingId,
        UserId? actorId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var scope = await PostgresCommandScope.CreateAsync(
            connections,
            executionContextAccessor,
            cancellationToken);

        await using (var command = scope.CreateCommand(
                         """
                         update open_item_adjustment_account_mappings
                         set is_active = false,
                             updated_by_user_id = @actor_id,
                             updated_at = now(),
                             deactivated_at = coalesce(deactivated_at, now())
                         where company_id = @company_id
                           and id = @mapping_id
                         returning id;
                         """))
        {
            command.Parameters.AddWithValue("company_id", companyId.Value);
            command.Parameters.AddWithValue("mapping_id", mappingId);
            command.Parameters.AddWithValue("actor_id", actorId.HasValue ? (object)actorId.Value.Value : DBNull.Value);

            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                return null;
            }
        }

        var mapping = await GetByIdAsync(scope, companyId, mappingId, cancellationToken);
        if (mapping is null)
        {
            return null;
        }

        await InsertAuditLogIfAvailableAsync(
            scope,
            mapping.CompanyId,
            mapping.MappingId,
            actorId,
            "open_item_adjustment_account_mapping_deactivated",
            new
            {
                mapping.MappingId,
                CompanyId = mapping.CompanyId,
                mapping.BookId,
                mapping.OpenItemType,
                mapping.AdjustmentType,
                mapping.AdjustmentAccountId
            },
            cancellationToken);

        return new OpenItemAdjustmentAccountMappingTransitionResult(
            mapping,
            "deactivate",
            "mapping_deactivated",
            "Open-item adjustment account mapping was deactivated; existing posted history remains unchanged.");
    }

    private static async Task<OpenItemAdjustmentAccountMappingRecord?> GetByIdAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        var companyBooksTableExists = await CompanyBooksTableExistsAsync(scope, cancellationToken);

        await using var command = scope.CreateCommand(
            companyBooksTableExists
                ? """
                  select
                    m.id,
                    m.company_id,
                    m.book_id,
                    b.book_code,
                    b.accounting_standard,
                    lower(m.open_item_type) as open_item_type,
                    lower(m.adjustment_type) as adjustment_type,
                    m.adjustment_account_id,
                    a.code as adjustment_account_code,
                    a.name as adjustment_account_name,
                    a.root_type as adjustment_account_root_type,
                    m.is_active,
                    m.created_by_user_id,
                    m.updated_by_user_id,
                    m.created_at,
                    m.updated_at,
                    m.deactivated_at
                  from open_item_adjustment_account_mappings m
                  join accounts a
                    on a.company_id = m.company_id
                   and a.id = m.adjustment_account_id
                  left join company_books b
                    on b.company_id = m.company_id
                   and b.id = m.book_id
                  where m.company_id = @company_id
                    and m.id = @mapping_id
                  limit 1;
                  """
                : """
                  select
                    m.id,
                    m.company_id,
                    m.book_id,
                    null::text as book_code,
                    null::text as accounting_standard,
                    lower(m.open_item_type) as open_item_type,
                    lower(m.adjustment_type) as adjustment_type,
                    m.adjustment_account_id,
                    a.code as adjustment_account_code,
                    a.name as adjustment_account_name,
                    a.root_type as adjustment_account_root_type,
                    m.is_active,
                    m.created_by_user_id,
                    m.updated_by_user_id,
                    m.created_at,
                    m.updated_at,
                    m.deactivated_at
                  from open_item_adjustment_account_mappings m
                  join accounts a
                    on a.company_id = m.company_id
                   and a.id = m.adjustment_account_id
                  where m.company_id = @company_id
                    and m.id = @mapping_id
                  limit 1;
                  """);

        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("mapping_id", mappingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMapping(reader)
            : null;
    }

    private static async Task EnsureBookBelongsToCompanyAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        if (!await CompanyBooksTableExistsAsync(scope, cancellationToken))
        {
            throw new InvalidOperationException("Book-specific adjustment account mappings require the company_books table.");
        }

        await using var command = scope.CreateCommand(
            """
            select 1
            from company_books
            where company_id = @company_id
              and id = @book_id
              and is_active = true
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("book_id", bookId);

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("The selected accounting book is not active for the company.");
        }
    }

    private static async Task EnsureAdjustmentAccountIsAllowedAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            """
            select 1
            from accounts
            where company_id = @company_id
              and id = @account_id
              and is_active = true
              and allow_manual_posting = true
              and root_type in ('revenue', 'cost_of_sales', 'expense')
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("account_id", accountId);

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException(
                "The adjustment account must be an active company account that allows manual posting and belongs to revenue, cost of sales, or expense.");
        }
    }

    private static async Task<bool> CompanyBooksTableExistsAsync(
        PostgresCommandScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = scope.CreateCommand(
            "select to_regclass('public.company_books') is not null;");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<Guid?> TryGetPrimaryBookIdAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (!await CompanyBooksTableExistsAsync(scope, cancellationToken))
        {
            return null;
        }

        await using var command = scope.CreateCommand(
            """
            select id
            from company_books
            where company_id = @company_id
              and is_active = true
              and is_primary = true
            order by effective_from desc, id asc
            limit 1;
            """);
        command.Parameters.AddWithValue("company_id", companyId.Value);

        return await command.ExecuteScalarAsync(cancellationToken) is Guid bookId
            ? bookId
            : null;
    }

    private static bool MatchesBookFilter(OpenItemAdjustmentAccountMappingRecord mapping, Guid? bookId) =>
        !bookId.HasValue || mapping.BookId == bookId.Value;

    private static bool MatchesPolicyScope(
        OpenItemAdjustmentAccountMappingRecord mapping,
        string? policyScope,
        Guid? primaryBookId)
    {
        if (string.IsNullOrWhiteSpace(policyScope))
        {
            return true;
        }

        var normalizedScope = NormalizePolicyScope(policyScope);
        return normalizedScope switch
        {
            "company_default" => mapping.BookId is null,
            "book_specific" => mapping.BookId is not null,
            "primary_execution" => mapping.BookId is null || (primaryBookId.HasValue && mapping.BookId == primaryBookId.Value),
            "governance_only" => mapping.BookId is not null && (!primaryBookId.HasValue || mapping.BookId != primaryBookId.Value),
            _ => true
        };
    }

    private static bool MatchesSearch(OpenItemAdjustmentAccountMappingRecord mapping, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var terms = searchText.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return terms.All(term =>
            ContainsInvariant(mapping.BookCode, term) ||
            ContainsInvariant(mapping.AccountingStandard, term) ||
            ContainsInvariant(mapping.OpenItemType, term) ||
            ContainsInvariant(mapping.AdjustmentType, term) ||
            ContainsInvariant(mapping.AdjustmentAccountCode, term) ||
            ContainsInvariant(mapping.AdjustmentAccountName, term) ||
            ContainsInvariant(mapping.AdjustmentAccountRootType, term) ||
            ContainsInvariant(mapping.IsActive ? "active" : "inactive", term) ||
            ContainsInvariant(mapping.BookId is null ? "company default" : "book specific", term));
    }

    private static bool ContainsInvariant(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static async Task InsertAuditLogIfAvailableAsync(
        PostgresCommandScope scope,
        CompanyId companyId,
        Guid mappingId,
        UserId? actorId,
        string action,
        object payload,
        CancellationToken cancellationToken)
    {
        await using (var existsCommand = scope.CreateCommand(
                         "select to_regclass('public.audit_logs') is not null;"))
        {
            if (await existsCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return;
            }
        }

        await using var command = scope.CreateCommand(
            """
            insert into audit_logs (
              id,
              company_id,
              actor_type,
              actor_id,
              entity_type,
              entity_id,
              action,
              payload
            )
            values (
              @id,
              @company_id,
              @actor_type,
              @actor_id,
              'open_item_adjustment_account_mapping',
              @entity_id,
              @action,
              @payload::jsonb
            );
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("actor_type", actorId.HasValue ? "user" : "system");
        command.Parameters.AddWithValue("actor_id", actorId.HasValue ? (object)actorId.Value.Value : DBNull.Value);
        command.Parameters.AddWithValue("entity_id", mappingId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static OpenItemAdjustmentAccountMappingRecord ReadMapping(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(reader.GetOrdinal("id")),
            CompanyId.Parse(reader.GetString(reader.GetOrdinal("company_id"))),
            reader.IsDBNull(reader.GetOrdinal("book_id")) ? null : reader.GetGuid(reader.GetOrdinal("book_id")),
            reader.IsDBNull(reader.GetOrdinal("book_code")) ? null : reader.GetString(reader.GetOrdinal("book_code")),
            reader.IsDBNull(reader.GetOrdinal("accounting_standard")) ? null : reader.GetString(reader.GetOrdinal("accounting_standard")),
            reader.GetString(reader.GetOrdinal("open_item_type")),
            reader.GetString(reader.GetOrdinal("adjustment_type")),
            reader.GetGuid(reader.GetOrdinal("adjustment_account_id")),
            reader.GetString(reader.GetOrdinal("adjustment_account_code")),
            reader.GetString(reader.GetOrdinal("adjustment_account_name")),
            reader.GetString(reader.GetOrdinal("adjustment_account_root_type")),
            reader.GetBoolean(reader.GetOrdinal("is_active")),
            reader.IsDBNull(reader.GetOrdinal("created_by_user_id")) ? null : UserId.Parse(reader.GetString(reader.GetOrdinal("created_by_user_id"))),
            reader.IsDBNull(reader.GetOrdinal("updated_by_user_id")) ? null : UserId.Parse(reader.GetString(reader.GetOrdinal("updated_by_user_id"))),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
            reader.IsDBNull(reader.GetOrdinal("deactivated_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("deactivated_at")));

    private static string NormalizeOpenItemType(string value)
    {
        var normalized = NormalizeToken(value, nameof(value));
        if (!AllowedOpenItemTypes.Contains(normalized))
        {
            throw new InvalidOperationException(
                "Open item type must be either ar_open_item or ap_open_item for adjustment account mapping.");
        }

        return normalized;
    }

    private static string NormalizeAdjustmentType(string value)
    {
        var normalized = NormalizeToken(value, nameof(value));
        if (!AllowedAdjustmentTypes.Contains(normalized))
        {
            throw new InvalidOperationException(
                "Adjustment type must be either write_off or small_balance_adjustment for adjustment account mapping.");
        }

        return normalized;
    }

    private static string NormalizePolicyScope(string value)
    {
        var normalized = NormalizeToken(value, nameof(value));
        if (!AllowedPolicyScopes.Contains(normalized))
        {
            throw new InvalidOperationException(
                "Policy scope must be one of company_default, book_specific, primary_execution, or governance_only for adjustment account mapping lookup.");
        }

        return normalized;
    }

    private static string NormalizeToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{parameterName} is required for open-item adjustment account mapping.");
        }

        return value.Trim().ToLowerInvariant();
    }
}
