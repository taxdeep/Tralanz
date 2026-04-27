using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;

namespace Citus.Accounting.Api.Initialization;

/// <summary>
/// Idempotent platform-tables + bootstrap-fixtures initializer.
///
/// Backstory: the Accounting API's account / tax-code / journal-entry / FX
/// inserts FK into platform tables (currency_catalog, companies, users,
/// company_memberships) created by
/// <see cref="PostgresPlatformFirstCompanyProvisioningRepository"/>. That
/// repository runs only when the SysAdmin First-Company Wizard fires — so
/// deployments using the dev "bootstrap" session shortcut never had those
/// tables created or seeded, and the very first Account write would fail
/// with a 23503 FK violation surfaced as
/// "Currency 'USD' is not in the platform currency catalog."
///
/// This initializer fixes that for every Accounting API startup:
///
///   1. Calls the existing platform-schema setup (idempotent
///      CREATE TABLE IF NOT EXISTS + seed currency_catalog) so the platform
///      tables exist with their FK constraints regardless of whether the
///      SysAdmin wizard has ever run on this database.
///
///   2. Idempotently INSERTs the bootstrap fixtures
///      (Northwind / Blue Harbor companies + Alice / Ben users + their
///      memberships) so the Guids hard-coded in
///      <c>Citus.Accounting.Api.BusinessSessionDirectory.GetDefaults*</c>
///      and in the Blazor shell's bootstrap-session payload resolve to
///      real persisted rows. Real owner-provisioned rows (created by the
///      SysAdmin wizard later) coexist via <c>ON CONFLICT DO NOTHING</c>.
///
/// Bootstrap users carry an obviously-fake password hash that no real
/// auth flow would ever match. They are dev-only fixtures.
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
        insert into companies (
            id, entity_number, legal_name, base_currency_code,
            multi_currency_enabled, status, country, account_code_length,
            entity_type, industry, fiscal_year_end_month, fiscal_year_end_day)
        values
          ('5e492df2-37ab-47df-a1bb-2d559c876cbc',
           'BS-NORTHWIND', 'Northwind Studio Ltd.', 'USD',
           true, 'active', 'United States', 4,
           'corporation', 'general_services', 12, 31),
          ('e56df08c-39ae-405b-8ed2-247b97d2f9f6',
           'BS-BLUEHARBOR', 'Blue Harbor Trading Co.', 'CAD',
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
    private readonly ILogger<PlatformBootstrapFixturesInitializer> _logger;

    public PlatformBootstrapFixturesInitializer(
        IPlatformFirstCompanyProvisioningRepository provisioningRepository,
        PlatformPostgresConnectionFactory connections,
        ILogger<PlatformBootstrapFixturesInitializer> logger)
    {
        _provisioningRepository = provisioningRepository;
        _connections = connections;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        // Step 1: idempotent platform schema (currency_catalog + seed,
        // companies, users, company_memberships, company_books, etc.)
        await _provisioningRepository.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        // Step 2: bootstrap fixtures, ON CONFLICT DO NOTHING so we never
        // overwrite real owner-provisioned data.
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = FixturesSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Platform bootstrap fixtures verified: currencies, companies (Northwind {Northwind}, Blue Harbor {BlueHarbor}), users (Alice {Alice}, Ben {Ben}), memberships.",
            NorthwindId, BlueHarborId, AliceId, BenId);
    }
}
