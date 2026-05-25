using Citus.Accounting.Application.Abstractions;
using Npgsql;

namespace Infrastructure.PostgreSQL.Accounts;

/// <summary>
/// PostgreSQL implementation of <see cref="IAccountStore"/>. Reads and
/// writes the existing <c>accounts</c> table from the migration draft
/// (line 645 of <c>CITUS_POSTGRESQL_MIGRATION_DRAFT.sql</c>).
///
/// Schema compatibility notes:
///   * <c>EnsureSchemaAsync</c> verifies the migration-installed table
///     shape instead of applying DDL from the application process.
///   * <c>entity_number</c> is auto-generated to satisfy the regex
///     <c>^EN[0-9]{4}[A-Z0-9]{5}$</c>.
///   * <c>is_system</c>-flagged rows reject UPDATE attempts so the
///     control accounts (AR, AP, FX revaluation, …) can't be quietly
///     mutated through the maintenance UI.
/// </summary>
public sealed class PostgreSqlAccountStore(PostgreSqlConnectionFactory connections) : IAccountStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await PostgreSqlSchemaChecks.EnsureTableColumnsAsync(
            connections,
            "accounts",
            new[]
            {
                "id",
                "company_id",
                "entity_number",
                "code",
                "name",
                "root_type",
                "detail_type",
                "is_active",
                "is_system",
                "is_system_default",
                "system_key",
                "system_role",
                "currency_code",
                "allow_manual_posting",
                "created_at",
                "updated_at",
                // Batch C + D additions (migration 2026-05-25-coa-subaccount-and-lock.sql).
                "parent_account_id",
                "locked_at",
                "locked_by_user_id"
            },
            "Account schema has not been installed. Apply database migrations before using chart-of-accounts records.",
            cancellationToken).ConfigureAwait(false);
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
                currency_code, allow_manual_posting, is_active, parent_account_id,
                created_at, updated_at)
            VALUES (
                @id, @company_id, @entity_number, @code, @name, @root_type, @detail_type,
                @currency_code, @allow_manual_posting, @is_active, @parent_account_id,
                @now, @now)
            RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                      is_active, is_system, is_system_default, system_key, system_role,
                      currency_code, allow_manual_posting, created_at, updated_at,
                      parent_account_id, locked_at, locked_by_user_id;
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
        command.Parameters.AddWithValue("parent_account_id",
            input.ParentAccountId.HasValue ? (object)input.ParentAccountId.Value : DBNull.Value);
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

        // Batch D: read the current row first so we can enforce the
        // lock predicate at application layer. We refuse the UPDATE
        // when locked_at IS NOT NULL AND any financial-truth field
        // changed. Re-parenting (parent_account_id) and is_active are
        // allowed even on locked accounts — see XML docs on
        // SetLockAsync for the rationale.
        AccountRecord? existing;
        await using (var loadCmd = connection.CreateCommand())
        {
            loadCmd.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
            loadCmd.Parameters.AddWithValue("company_id", companyId.Value);
            loadCmd.Parameters.AddWithValue("id", accountId);
            await using var loadReader = await loadCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            existing = await loadReader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(loadReader) : null;
        }
        if (existing is null)
        {
            return null;
        }
        if (existing.IsSystem)
        {
            // Mirrors the pre-Batch-D WHERE clause guard. Surface as
            // a "no row returned" so callers see the same shape.
            return null;
        }
        if (existing.LockedAt is not null && AnyFinancialFieldChanged(existing, input))
        {
            throw new InvalidOperationException(
                $"Account {existing.Code} is locked. Unlock it before changing the code, name, type, detail, currency, or manual-posting flag.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounts
               SET code = @code,
                   name = @name,
                   root_type = @root_type,
                   detail_type = @detail_type,
                   currency_code = @currency_code,
                   allow_manual_posting = @allow_manual_posting,
                   is_active = @is_active,
                   parent_account_id = @parent_account_id,
                   updated_at = @now
             WHERE company_id = @company_id AND id = @id AND is_system = FALSE
            RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                      is_active, is_system, is_system_default, system_key, system_role,
                      currency_code, allow_manual_posting, created_at, updated_at,
                      parent_account_id, locked_at, locked_by_user_id;
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
        command.Parameters.AddWithValue("parent_account_id",
            input.ParentAccountId.HasValue ? (object)input.ParentAccountId.Value : DBNull.Value);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>
    /// Batch D: the lock predicate compares the financial-truth fields
    /// of the existing row against the incoming patch. Returns true if
    /// any changed. Trim + case-fold mirror the persistence rules so
    /// "  CASH " vs "Cash" doesn't read as a change.
    /// </summary>
    private static bool AnyFinancialFieldChanged(AccountRecord existing, AccountUpsertInput input)
    {
        var codeChanged = !string.Equals(existing.Code, input.Code?.Trim(), StringComparison.Ordinal);
        var nameChanged = !string.Equals(existing.Name, input.Name?.Trim(), StringComparison.Ordinal);
        var rootChanged = !string.Equals(existing.RootType, input.RootType, StringComparison.OrdinalIgnoreCase);
        var detailChanged = !string.Equals(existing.DetailType ?? string.Empty, input.DetailType?.Trim() ?? string.Empty, StringComparison.Ordinal);
        var existingCcy = existing.CurrencyCode?.Trim() ?? string.Empty;
        var incomingCcy = input.CurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var ccyChanged = !string.Equals(existingCcy, incomingCcy, StringComparison.Ordinal);
        var postingChanged = existing.AllowManualPosting != input.AllowManualPosting;
        return codeChanged || nameChanged || rootChanged || detailChanged || ccyChanged || postingChanged;
    }

    public async Task<AccountRecord?> SetLockAsync(
        CompanyId companyId,
        Guid accountId,
        AccountLockInput input,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Read first so the audit_log payload can record the prior state
        // and so we can no-op when the lock is already in the desired state.
        AccountRecord? before;
        await using (var loadCmd = connection.CreateCommand())
        {
            loadCmd.Transaction = transaction;
            loadCmd.CommandText = SelectColumns + " WHERE company_id = @company_id AND id = @id LIMIT 1;";
            loadCmd.Parameters.AddWithValue("company_id", companyId.Value);
            loadCmd.Parameters.AddWithValue("id", accountId);
            await using var loadReader = await loadCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            before = await loadReader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(loadReader) : null;
        }
        if (before is null) return null;

        var isAlreadyLocked = before.LockedAt is not null;
        if (input.Lock == isAlreadyLocked)
        {
            // No state change — surface the row as-is, skip audit.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return before;
        }

        AccountRecord? after;
        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE accounts
                   SET locked_at = @locked_at,
                       locked_by_user_id = @locked_by_user_id,
                       updated_at = @now
                 WHERE company_id = @company_id AND id = @id AND is_system = FALSE
                RETURNING id, company_id, entity_number, code, name, root_type, detail_type,
                          is_active, is_system, is_system_default, system_key, system_role,
                          currency_code, allow_manual_posting, created_at, updated_at,
                          parent_account_id, locked_at, locked_by_user_id;
                """;
            updateCmd.Parameters.AddWithValue("id", accountId);
            updateCmd.Parameters.AddWithValue("company_id", companyId.Value);
            updateCmd.Parameters.AddWithValue("locked_at",
                input.Lock ? (object)now : DBNull.Value);
            updateCmd.Parameters.AddWithValue("locked_by_user_id",
                input.Lock && input.ActorUserId is { } u && u.Value is not null
                    ? (object)u.Value
                    : DBNull.Value);
            updateCmd.Parameters.AddWithValue("now", now);
            await using var updateReader = await updateCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            after = await updateReader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(updateReader) : null;
        }

        if (after is null)
        {
            // System account — silently ignore (matches the pre-existing
            // is_system guard's "return null" contract).
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Append a row to audit_logs so the lock / unlock event is
        // traceable. payload includes prior state + actor.
        await using (var auditCmd = connection.CreateCommand())
        {
            auditCmd.Transaction = transaction;
            auditCmd.CommandText = """
                INSERT INTO audit_logs (
                    company_id, actor_type, actor_id,
                    entity_type, entity_id, action, payload)
                VALUES (
                    @company_id, 'user', @actor_id,
                    'account', @entity_id, @action, @payload::jsonb);
                """;
            auditCmd.Parameters.AddWithValue("company_id", companyId.Value);
            auditCmd.Parameters.AddWithValue("actor_id",
                input.ActorUserId is { } u && u.Value is not null ? (object)u.Value : DBNull.Value);
            auditCmd.Parameters.AddWithValue("entity_id", accountId);
            auditCmd.Parameters.AddWithValue("action",
                input.Lock ? "account_locked" : "account_unlocked");
            auditCmd.Parameters.AddWithValue("payload",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    account_id = accountId,
                    account_code = before.Code,
                    prior_locked_at = before.LockedAt,
                    prior_locked_by_user_id = before.LockedByUserId,
                    new_locked_at = after.LockedAt,
                    new_locked_by_user_id = after.LockedByUserId
                }));
            await auditCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return after;
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
                      currency_code, allow_manual_posting, created_at, updated_at,
                      parent_account_id, locked_at, locked_by_user_id;
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
                      currency_code, allow_manual_posting, created_at, updated_at,
                      parent_account_id, locked_at, locked_by_user_id;
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
               currency_code, allow_manual_posting, created_at, updated_at,
               parent_account_id, locked_at, locked_by_user_id
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
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(15),
        ParentAccountId: reader.IsDBNull(16) ? null : reader.GetGuid(16),
        LockedAt: reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        LockedByUserId: reader.IsDBNull(18) ? null : reader.GetString(18).Trim());
}
