using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Accounts;

/// <summary>
/// PostgreSQL implementation of <see cref="IAccountStore"/>. Reads and
/// writes the existing <c>accounts</c> table from the migration draft
/// (line 645 of <c>CITUS_POSTGRESQL_MIGRATION_DRAFT.sql</c>).
///
/// Schema compatibility notes:
///   * <c>EnsureSchemaAsync</c> uses <c>CREATE TABLE IF NOT EXISTS</c>
///     so a deploy that already loaded the migration draft is a no-op
///     and a fresh dev database gets a structurally-equivalent shape.
///     The dev variant intentionally omits the FK to
///     <c>currency_catalog</c> (which the migration draft loads later);
///     production keeps that FK from the canonical schema.
///   * <c>entity_number</c> is auto-generated to satisfy the regex
///     <c>^EN[0-9]{4}[0-9]{8}$</c>.
///   * <c>is_system</c>-flagged rows reject UPDATE attempts so the
///     control accounts (AR, AP, FX revaluation, …) can't be quietly
///     mutated through the maintenance UI.
/// </summary>
public sealed class PostgreSqlAccountStore(PostgreSqlConnectionFactory connections) : IAccountStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts (
                id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id               UUID NOT NULL,
                entity_number            TEXT NOT NULL,
                code                     TEXT NOT NULL,
                name                     TEXT NOT NULL,
                root_type                TEXT NOT NULL,
                detail_type              TEXT NOT NULL DEFAULT '',
                is_active                BOOLEAN NOT NULL DEFAULT TRUE,
                is_system                BOOLEAN NOT NULL DEFAULT FALSE,
                is_system_default        BOOLEAN NOT NULL DEFAULT FALSE,
                system_key               TEXT NULL,
                system_role              TEXT NULL,
                currency_code            CHAR(3) NULL,
                allow_manual_posting     BOOLEAN NOT NULL DEFAULT TRUE,
                created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uq_accounts_entity_number ON accounts (entity_number);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_accounts_company_code ON accounts (company_id, code);
            CREATE INDEX IF NOT EXISTS idx_accounts_company_active ON accounts (company_id, is_active);
            CREATE INDEX IF NOT EXISTS idx_accounts_company_root ON accounts (company_id, root_type);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccountRecord>> ListAsync(
        CompanyId companyId,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var items = new List<AccountRecord>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeInactive
            ? SelectColumns + " WHERE company_id = @company_id ORDER BY root_type, code;"
            : SelectColumns + " WHERE company_id = @company_id AND is_active = TRUE ORDER BY root_type, code;";
        command.Parameters.AddWithValue("company_id", companyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }
        return items;
    }

    public async Task<AccountRecord?> GetByIdAsync(
        CompanyId companyId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<AccountRecord> CreateAsync(
        CompanyId companyId,
        AccountUpsertInput input,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var entityNumber = GenerateEntityNumber();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (
                id, company_id, entity_number, code, name, root_type, detail_type,
                currency_code, allow_manual_posting, is_active, created_at, updated_at)
            VALUES (
                @id, @company_id, @entity_number, @code, @name, @root_type, @detail_type,
                @currency_code, @allow_manual_posting, @is_active, @now, @now)
            RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                      is_active, is_system, is_system_default, system_key, system_role,
                      currency_code, allow_manual_posting, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("root_type", input.RootType);
        command.Parameters.AddWithValue("detail_type", input.DetailType?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("currency_code",
            string.IsNullOrWhiteSpace(input.CurrencyCode) ? (object)DBNull.Value : input.CurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("allow_manual_posting", input.AllowManualPosting);
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Account insert returned no row.");
        }
        return Map(reader);
    }

    public async Task<AccountRecord?> UpdateAsync(
        CompanyId companyId,
        Guid accountId,
        AccountUpsertInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Reject system rows. AND is_system = FALSE prevents quiet
        // mutations of control accounts (AR, AP, FX revaluation).
        command.CommandText = """
            UPDATE accounts
               SET code = @code,
                   name = @name,
                   root_type = @root_type,
                   detail_type = @detail_type,
                   currency_code = @currency_code,
                   allow_manual_posting = @allow_manual_posting,
                   is_active = @is_active,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id AND is_system = FALSE
            RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                      is_active, is_system, is_system_default, system_key, system_role,
                      currency_code, allow_manual_posting, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("root_type", input.RootType);
        command.Parameters.AddWithValue("detail_type", input.DetailType?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("currency_code",
            string.IsNullOrWhiteSpace(input.CurrencyCode) ? (object)DBNull.Value : input.CurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("allow_manual_posting", input.AllowManualPosting);
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<AccountRecord?> SetActiveAsync(
        CompanyId companyId,
        Guid accountId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // System accounts cannot be deactivated by the maintenance UI;
        // the Posting Engine relies on AR/AP/FX control rows being
        // resolvable.
        command.CommandText = """
            UPDATE accounts
               SET is_active = @is_active,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id AND is_system = FALSE
            RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                      is_active, is_system, is_system_default, system_key, system_role,
                      currency_code, allow_manual_posting, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("is_active", isActive);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<AccountRecord?> SeedSystemAccountAsync(
        CompanyId companyId,
        AccountSeedInput input,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var entityNumber = GenerateEntityNumber();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // ON CONFLICT DO NOTHING keeps seed runs idempotent: the second
        // application of the same template no-ops on every row instead of
        // raising 23505 (we still need that branch on the user-facing
        // CreateAsync where the API wants to surface the conflict).
        command.CommandText = """
            INSERT INTO accounts (
                id, company_id, entity_number, code, name, root_type, detail_type,
                currency_code, allow_manual_posting, is_active,
                is_system, is_system_default, system_key, system_role,
                created_at, updated_at)
            VALUES (
                @id, @company_id, @entity_number, @code, @name, @root_type, @detail_type,
                @currency_code, @allow_manual_posting, @is_active,
                @is_system, @is_system_default, @system_key, @system_role,
                @now, @now)
            ON CONFLICT (company_id, code) DO NOTHING
            RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                      is_active, is_system, is_system_default, system_key, system_role,
                      currency_code, allow_manual_posting, created_at, updated_at;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("entity_number", entityNumber);
        command.Parameters.AddWithValue("code", input.Code.Trim());
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("root_type", input.RootType);
        command.Parameters.AddWithValue("detail_type", input.DetailType?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("currency_code",
            string.IsNullOrWhiteSpace(input.CurrencyCode) ? (object)DBNull.Value : input.CurrencyCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("allow_manual_posting", input.AllowManualPosting);
        command.Parameters.AddWithValue("is_active", input.IsActive);
        command.Parameters.AddWithValue("is_system", input.IsSystem);
        command.Parameters.AddWithValue("is_system_default", input.IsSystemDefault);
        command.Parameters.AddWithValue("system_key",
            string.IsNullOrWhiteSpace(input.SystemKey) ? (object)DBNull.Value : input.SystemKey);
        command.Parameters.AddWithValue("system_role",
            string.IsNullOrWhiteSpace(input.SystemRole) ? (object)DBNull.Value : input.SystemRole);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>
    /// Builds an entity number that satisfies the migration-draft regex
    /// <c>^EN[0-9]{4}[0-9]{8}$</c>: <c>EN{4-digit-year}{8-digit-random}</c>.
    /// </summary>
    private static string GenerateEntityNumber()
    {
        var year = DateTime.UtcNow.Year;
        var seed = Random.Shared.Next(0, (int)EntityNumber.MaxOrdinal + 1);
        return EntityNumber.Create(year, seed).Value;
    }

    private const string SelectColumns = """
        SELECT id, company_id, entity_number, code, name, root_type, detail_type,
               is_active, is_system, is_system_default, system_key, system_role,
               currency_code, allow_manual_posting, created_at, updated_at
        FROM accounts
        """;

    private static AccountRecord Map(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        CompanyId: CompanyId.Parse(reader.GetString(1)),
        EntityNumber: reader.GetString(2),
        Code: reader.GetString(3),
        Name: reader.GetString(4),
        RootType: reader.GetString(5),
        DetailType: reader.IsDBNull(6) ? null : reader.GetString(6),
        IsActive: reader.GetBoolean(7),
        IsSystem: reader.GetBoolean(8),
        IsSystemDefault: reader.GetBoolean(9),
        SystemKey: reader.IsDBNull(10) ? null : reader.GetString(10),
        SystemRole: reader.IsDBNull(11) ? null : reader.GetString(11),
        CurrencyCode: reader.IsDBNull(12) ? null : reader.GetString(12).Trim(),
        AllowManualPosting: reader.GetBoolean(13),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(14),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15));
}
