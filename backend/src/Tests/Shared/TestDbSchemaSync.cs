using Npgsql;

namespace Tests.Shared;

/// <summary>
/// Runs the production migration chain against the local test
/// PostgreSQL once per test-process so AR / AP / GL / CompanyAccess
/// suites stay in sync with the schema additions that lifecycle
/// migrations (deploy/migrations/*.sql) introduce.
///
/// Why this exists: each Postgres-backed test project connects directly
/// to the developer's local <c>citus_accounting</c> database via
/// <c>CITUS_ACCOUNTING_DB</c>. The local DB was originally seeded from
/// <c>CITUS_POSTGRESQL_MIGRATION_DRAFT.sql</c> and ad-hoc C# bootstraps;
/// subsequent <c>deploy/migrations/*.sql</c> files only ever run on
/// production (via deploy/ubuntu24/common.sh's
/// <c>apply_pending_migrations</c>). So new columns / constraints /
/// trigger functions added by recent migrations are missing locally,
/// and the tests trip <c>42703</c> "column X does not exist" or
/// equivalent errors.
///
/// This helper mirrors the production runner: tracks applied
/// migrations in a <c>schema_migrations</c> table (same shape as
/// production), skips entries already recorded, applies the rest in
/// alphabetical order. Every migration in <c>deploy/migrations/</c>
/// MUST be idempotent (re-running adds nothing) so it is safe to
/// re-apply on a dev DB that may already have parts of it from older
/// bootstraps.
///
/// Failure mode: if one migration errors (e.g., a CHECK / FK that
/// duplicates an existing one with a tiny shape mismatch), the error
/// is logged to stderr and the loop continues. The migration is NOT
/// recorded as applied, so a subsequent run re-tries it. This avoids
/// blocking the whole test suite on a single brittle file.
/// </summary>
public static class TestDbSchemaSync
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static volatile bool _applied;

    /// <summary>
    /// Auto-apply pending <c>deploy/migrations/*.sql</c> ONCE per test assembly
    /// at load time, so DB-backed suites self-heal after new migrations land
    /// instead of tripping <c>42703 "column X does not exist"</c> against a
    /// migration-behind dev DB. Guards on the test-DB env var and swallows all
    /// errors, so non-DB / skipped runs (and any sync hiccup) are unaffected —
    /// the assembly always finishes loading. Each test project that links this
    /// file gets its own initializer; the <c>_applied</c>/<c>_gate</c> guard in
    /// <see cref="EnsureMigrationsAppliedAsync"/> keeps it idempotent in-process.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AutoApplyPendingMigrations()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB")))
        {
            return;
        }

        try
        {
            // FULLY SYNCHRONOUS apply. A ModuleInitializer runs during assembly
            // load, where blocking on async work (even via Task.Run) can deadlock
            // on a cold/starved threadpool — so we use sync Npgsql calls only.
            ApplyPendingMigrationsSync();
        }
        catch
        {
            // Never block assembly load on a migration-sync hiccup; the
            // individual DB tests surface a clearer error if the schema is
            // genuinely wrong.
        }
    }

    private static void ApplyPendingMigrationsSync()
    {
        if (_applied) return;
        _gate.Wait();
        try
        {
            if (_applied) return;

            var migrationsDir = FindMigrationsDir();
            if (migrationsDir is null) return;

            using var connection = new NpgsqlConnection(GetConnectionString());
            connection.Open();

            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText =
                    "create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now());";
                ddl.ExecuteNonQuery();
            }

            var applied = new HashSet<string>(StringComparer.Ordinal);
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "select name from schema_migrations;";
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    applied.Add(reader.GetString(0));
                }
            }

            foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (applied.Contains(name)) continue;

                try
                {
                    using (var apply = connection.CreateCommand())
                    {
                        apply.CommandText = File.ReadAllText(file);
                        apply.ExecuteNonQuery();
                    }

                    using var record = connection.CreateCommand();
                    record.CommandText = "insert into schema_migrations(name) values (@n) on conflict do nothing;";
                    record.Parameters.AddWithValue("n", name);
                    record.ExecuteNonQuery();
                }
                catch (PostgresException ex)
                {
                    // The failed migration may have left an aborted transaction
                    // open on the shared connection (its trailing COMMIT never
                    // ran), which would cascade 25P02 into every later migration.
                    // Roll back so the next one starts clean.
                    try
                    {
                        using var rollback = connection.CreateCommand();
                        rollback.CommandText = "rollback;";
                        rollback.ExecuteNonQuery();
                    }
                    catch
                    {
                        // No open transaction to roll back — fine.
                    }

                    Console.Error.WriteLine(
                        $"[TestDbSchemaSync] {name} failed ({ex.SqlState}): {ex.MessageText}");
                }
            }

            _applied = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Test-project connection string. Mirrors the same env-var
    /// fallback the individual suites use so any one of them can call
    /// us without coordination.</summary>
    public static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CITUS_POSTGRESQL_INTEGRATION_TEST_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "DB-backed test skipped: set CITUS_POSTGRESQL_INTEGRATION_TEST_DB to a dedicated test database to run it.");

        return connectionString!;
    }

    /// <summary>Apply every pending migration once per process. Safe to
    /// call from a <c>[ModuleInitializer]</c> or directly inside a
    /// fixture's <c>InitializeAsync</c>.</summary>
    public static async Task EnsureMigrationsAppliedAsync(CancellationToken cancellationToken = default)
    {
        if (_applied) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_applied) return;
            await ApplyAsync(cancellationToken);
            _applied = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var migrationsDir = FindMigrationsDir();
        if (migrationsDir is null)
        {
            // Test binary not in a repo checkout (CI runner artifact-
            // shipping scenario). Caller should have a different
            // bootstrap path; we silently no-op.
            return;
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            // No local Postgres reachable. Tests that need it will
            // fail with a clearer error than ours; don't mask it.
            return;
        }

        // Mirror of deploy/ubuntu24/common.sh apply_pending_migrations.
        await ExecuteAsync(
            connection,
            """
            create table if not exists schema_migrations (
              name text primary key,
              applied_at timestamptz not null default now()
            );
            """,
            cancellationToken);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "select name from schema_migrations;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetString(0));
            }
        }

        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (applied.Contains(name))
            {
                continue;
            }

            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            try
            {
                await ExecuteAsync(connection, sql, cancellationToken);
                await using var record = connection.CreateCommand();
                record.CommandText =
                    "insert into schema_migrations(name) values (@n) on conflict do nothing;";
                record.Parameters.AddWithValue("n", name);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex)
            {
                // Roll back any aborted transaction the failed migration left
                // open on the shared connection, so it doesn't cascade 25P02
                // into every later migration (see ApplyPendingMigrationsSync).
                try
                {
                    await using var rollback = connection.CreateCommand();
                    rollback.CommandText = "rollback;";
                    await rollback.ExecuteNonQueryAsync(cancellationToken);
                }
                catch
                {
                    // No open transaction to roll back — fine.
                }

                // Soft-fail: log and continue. The migration stays
                // NOT recorded so a later run retries it once the
                // shape mismatch is fixed in the migration file.
                Console.Error.WriteLine(
                    $"[TestDbSchemaSync] {name} failed ({ex.SqlState}): {ex.MessageText}");
            }
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Walk up from the running binary until we find
    /// <c>deploy/migrations/</c>. Returns null if not found (e.g. CI
    /// artifact ship without the repo tree).</summary>
    private static string? FindMigrationsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "migrations");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
