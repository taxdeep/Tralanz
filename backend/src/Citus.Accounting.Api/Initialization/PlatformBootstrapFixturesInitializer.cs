using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Citus.Accounting.Api.Initialization;

/// <summary>
/// Idempotent platform-tables initializer with environment-gated dev fixtures.
///
/// Two distinct concerns, intentionally split:
///
///   1. <b>Platform schema + reference data</b> — runs UNCONDITIONALLY in
///      every environment, because the Accounting API's account / tax-code
///      / journal-entry / FX inserts FK into <c>currency_catalog</c>,
///      <c>companies</c>, <c>users</c>, <c>company_memberships</c>. Without
///      these tables (and the ISO 4217 currency rows), every business
///      write fails with 23503. Calls
///      <see cref="PostgresPlatformFirstCompanyProvisioningRepository.EnsureSchemaAsync"/>,
///      which is idempotent — every CREATE TABLE / INSERT in that path uses
///      IF NOT EXISTS / ON CONFLICT DO NOTHING.
///
///   2. <b>Dev bootstrap fixtures</b> (Northwind / Blue Harbor companies +
///      Alice / Ben users + their memberships) — gated behind
///      <see cref="BusinessBootstrapOptions"/>. By default these seed only
///      when running in <c>Development</c>, so a fresh production install
///      stays clean of demo data. Operators can opt in for staging / preview
///      with <c>BusinessBootstrap:Fixtures:AllowInNonDevelopment=true</c>.
///
/// Bootstrap users carry an obviously-fake password hash that no real auth
/// flow would ever match. They are dev-only fixtures.
/// </summary>
public sealed class PlatformBootstrapFixturesInitializer
{
    private static readonly Guid NorthwindId = Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc");
    private static readonly Guid BlueHarborId = Guid.Parse("e56df08c-39ae-405b-8ed2-247b97d2f9f6");
    private static readonly Guid AliceId = Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d");
    private static readonly Guid BenId = Guid.Parse("3512739f-2af3-41f5-8fd4-d648d913a274");

    private const string FixturesSql = """
        -- Bootstrap users: deliberately disabled password hash so real auth
        -- never accepts these accounts. The IDs match the static directory.
        insert into users (id, email, username, display_name, password_hash, status)
        values
          ('7bd0e908-cfe7-4f7b-8a0d-f19292e4186d', 'alice.rowan@northwind.example',
           'alice.rowan', 'Alice Rowan',
           'pbkdf2-sha256$bootstrap-no-real-auth$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==',
           'active'),
          ('3512739f-2af3-41f5-8fd4-d648d913a274', 'ben.mercer@blueharbor.example',
           'ben.mercer', 'Ben Mercer',
           'pbkdf2-sha256$bootstrap-no-real-auth$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==',
           'active')
        on conflict (id) do nothing;

        -- Bootstrap companies. base_currency_code FK to currency_catalog
        -- is satisfied because EnsureSchemaAsync seeded USD/CAD already.
        -- Entity numbers must match the companies_entity_number_format_chk
        -- regex (EN<year><8-digit-sequence>); we reserve year 2000 sequence
        -- numbers 1..2 for bootstrap fixtures so they never collide with
        -- the real First-Company-Wizard sequence (uses the current year).
        insert into companies (
            id, entity_number, legal_name, base_currency_code,
            multi_currency_enabled, status, country, account_code_length,
            entity_type, industry, fiscal_year_end_month, fiscal_year_end_day)
        values
          ('5e492df2-37ab-47df-a1bb-2d559c876cbc',
           'EN20000000000001', 'Northwind Studio Ltd.', 'USD',
           true, 'active', 'United States', 4,
           'corporation', 'general_services', 12, 31),
          ('e56df08c-39ae-405b-8ed2-247b97d2f9f6',
           'EN20000000000002', 'Blue Harbor Trading Co.', 'CAD',
           false, 'active', 'Canada', 4,
           'corporation', 'trading', 12, 31)
        on conflict (id) do nothing;

        -- Bootstrap memberships: Alice owns Northwind, Ben works AP at Blue Harbor.
        insert into company_memberships (company_id, user_id, role, is_active, permissions)
        values
          ('5e492df2-37ab-47df-a1bb-2d559c876cbc',
           '7bd0e908-cfe7-4f7b-8a0d-f19292e4186d', 'owner', true, '["owner","reports"]'::jsonb),
          ('e56df08c-39ae-405b-8ed2-247b97d2f9f6',
           '3512739f-2af3-41f5-8fd4-d648d913a274', 'user', true, '["user","ap"]'::jsonb)
        on conflict do nothing;
        """;

    private readonly IPlatformFirstCompanyProvisioningRepository _provisioningRepository;
    private readonly PlatformPostgresConnectionFactory _connections;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly BusinessBootstrapOptions _options;
    private readonly ILogger<PlatformBootstrapFixturesInitializer> _logger;

    public PlatformBootstrapFixturesInitializer(
        IPlatformFirstCompanyProvisioningRepository provisioningRepository,
        PlatformPostgresConnectionFactory connections,
        IHostEnvironment hostEnvironment,
        IOptions<BusinessBootstrapOptions> options,
        ILogger<PlatformBootstrapFixturesInitializer> logger)
    {
        _provisioningRepository = provisioningRepository;
        _connections = connections;
        _hostEnvironment = hostEnvironment;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        // Step 1: idempotent platform schema (currency_catalog + USD/CAD/etc.
        // seed, companies, users, company_memberships, company_books, …).
        // ALWAYS runs — these are real platform reference data, not demo
        // fixtures. Production needs the tables and the currency rows to
        // satisfy FK targets on every business write.
        await _provisioningRepository.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        // Step 2: dev fixtures (Alice / Ben / Northwind / Blue Harbor +
        // memberships). Gated behind BusinessBootstrap:Fixtures:Enabled
        // and the IsDevelopment / AllowInNonDevelopment switch, so a fresh
        // production install stays clean of demo identities.
        var isDevelopment = _hostEnvironment.IsDevelopment();
        if (!_options.Fixtures.IsActive(isDevelopment))
        {
            _logger.LogInformation(
                "Platform schema verified; bootstrap fixtures skipped (Environment={Environment}, Fixtures.Enabled={Enabled}, Fixtures.AllowInNonDevelopment={AllowInNonDev}).",
                _hostEnvironment.EnvironmentName,
                _options.Fixtures.Enabled,
                _options.Fixtures.AllowInNonDevelopment);
            return;
        }

        // Fixtures are convenience-only (dev shortcut). If the SQL fails for
        // any reason — schema drift in a future migration adds a check we
        // didn't anticipate, a column rename, etc. — we log and continue
        // rather than core-dump the API. The platform schema itself is
        // already up at this point, so business writes can still succeed
        // for any user that does have real persisted membership; only the
        // bootstrap session shortcut would break, and that's already a dev
        // convenience.
        try
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = FixturesSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Platform bootstrap fixtures seeded (Environment={Environment}): currencies, companies (Northwind {Northwind}, Blue Harbor {BlueHarbor}), users (Alice {Alice}, Ben {Ben}), memberships.",
                _hostEnvironment.EnvironmentName,
                NorthwindId, BlueHarborId, AliceId, BenId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Platform bootstrap fixtures could not be seeded; the API will start without them. Bootstrap-session writes may fail until this is resolved or real users are provisioned.");
        }
    }
}
