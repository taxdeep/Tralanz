# =====================================================================
# tools/sync-test-db.ps1 — bring the local citus_accounting test DB
# in line with deploy/migrations/*.sql
# =====================================================================
#
# Why this exists: each Postgres-backed test project connects directly
# to the developer's local citus_accounting database via the
# CITUS_ACCOUNTING_DB env var (default
# Host=localhost;Port=5432;...;Password=change-me). The local DB
# inherited its baseline from CITUS_POSTGRESQL_MIGRATION_DRAFT.sql +
# ad-hoc C# bootstraps; subsequent deploy/migrations/*.sql files run
# ONLY on production deploys (via deploy/ubuntu24/common.sh's
# apply_pending_migrations). So columns / constraints / triggers
# added by recent migrations (e.g., invoice_lines.task_id, the bank
# reconciliation R-1 schema, etc.) are missing on a stale dev DB,
# and any Postgres-backed test that exercises those columns trips
# 42703 "column does not exist" or 42P01 "relation does not exist".
#
# Run this once after pulling new migrations, before running the
# affected test suites:
#   pwsh tools/sync-test-db.ps1
#
# Idempotent: skips migrations already recorded in
# schema_migrations. Soft-fails on per-migration errors so one
# broken file doesn't abort the chain. Errors print to stderr.
#
# Known dev-environment-specific failures (safely ignored):
#   * 2026-05-22-m13-row-level-security.sql — needs the
#     'citus_app' role that only the production install.sh creates.
#     Local dev runs as superuser; RLS policies attach to
#     citus_app, so this migration's GRANT statements fail with
#     42704. The RLS infrastructure is defense-in-depth; tests that
#     don't enter strict-mode RLS continue to work.
# =====================================================================

param(
    [string]$ConnectionString = $(if ($env:CITUS_ACCOUNTING_DB) { $env:CITUS_ACCOUNTING_DB } else { "Host=localhost;Port=5432;Database=citus_accounting;Username=postgres;Password=change-me" }),
    [string]$MigrationsDir = (Join-Path $PSScriptRoot ".." "deploy/migrations" | Resolve-Path).Path,
    [switch]$SeedC000001Owner
)

$ErrorActionPreference = "Stop"

$dotnet = (Get-Command dotnet).Source
$tempScript = Join-Path $env:TEMP "sync_test_db_runner.cs"

$source = @'
#:property AssemblyName=sync_test_db
#:package Npgsql@8.*
using Npgsql; using System; using System.IO; using System.Linq;
using System.Threading.Tasks; using System.Collections.Generic;

var cs = args[0];
var dir = args[1];
var seedOwner = args.Length > 2 && args[2] == "--seed-owner";

if (seedOwner) {
    await using var c = new NpgsqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = c.CreateCommand();
    // Permission-foundation migration aborts if any company has zero
    // active owners. Local dev fixtures don't always have one; seed
    // a placeholder so the migration can run.
    cmd.CommandText = @"
        insert into company_memberships (id, company_id, user_id, role, permissions, is_active, is_owner)
        select gen_random_uuid(), c.id, u.id, 'owner', '[]'::jsonb, true, true
        from companies c
        cross join lateral (
            select id from users order by id limit 1
        ) u
        where not exists (
            select 1 from company_memberships m
            where m.company_id = c.id and m.is_owner = true and m.is_active = true
        );";
    var n = await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"Seeded {n} owner row(s) for companies without an active owner.");
}

var applied = new HashSet<string>();
await using (var conn = new NpgsqlConnection(cs)) {
    await conn.OpenAsync();
    await using var c = conn.CreateCommand();
    c.CommandText = @"
        create table if not exists schema_migrations (
            name text primary key, applied_at timestamptz not null default now()
        );
        select name from schema_migrations;";
    using var r = await c.ExecuteReaderAsync();
    while (await r.ReadAsync()) applied.Add(r.GetString(0));
}
Console.WriteLine($"Already applied: {applied.Count}");

int ok = 0, fail = 0;
foreach (var f in Directory.GetFiles(dir, "*.sql").OrderBy(x => x)) {
    var n = Path.GetFileName(f);
    if (applied.Contains(n)) continue;
    var sql = await File.ReadAllTextAsync(f);
    // Fresh connection per migration so a transaction abort in one
    // doesn't poison subsequent migrations.
    await using var conn = new NpgsqlConnection(cs);
    await conn.OpenAsync();
    try {
        await using var c = conn.CreateCommand();
        c.CommandText = sql;
        await c.ExecuteNonQueryAsync();
        await using var ins = conn.CreateCommand();
        ins.CommandText = "insert into schema_migrations(name) values (@n) on conflict do nothing;";
        ins.Parameters.AddWithValue("n", n);
        await ins.ExecuteNonQueryAsync();
        ok++;
        Console.WriteLine($"OK   {n}");
    } catch (PostgresException ex) {
        fail++;
        Console.Error.WriteLine($"FAIL {n}: {ex.SqlState} {ex.MessageText.Substring(0, Math.Min(160, ex.MessageText.Length))}");
    }
}
Console.WriteLine($"summary: applied={ok}, failed={fail}");
'@

Set-Content -Path $tempScript -Value $source -Encoding utf8

$runArgs = @($tempScript, $ConnectionString, $MigrationsDir)
if ($SeedC000001Owner) { $runArgs += "--seed-owner" }
& $dotnet run @runArgs
