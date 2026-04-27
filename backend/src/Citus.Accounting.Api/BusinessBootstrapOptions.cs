namespace Citus.Accounting.Api;

/// <summary>
/// Gates the Accounting API's dev-only bootstrap fixtures (Alice / Ben /
/// Northwind / Blue Harbor) from being seeded outside Development.
///
/// Mirrors the existing <c>SysAdminAuthOptions.BootstrapOptions</c> shape
/// for consistency. Defaults:
///   - Development environment → fixtures seeded (Enabled=true).
///   - Anything else → fixtures NOT seeded, unless an operator explicitly
///     opts in by setting <c>AllowInNonDevelopment=true</c> for a staging
///     / preview deploy.
///
/// The platform-schema setup itself (CREATE TABLE IF NOT EXISTS for
/// currency_catalog, companies, users, etc., plus the ISO 4217 currency
/// rows) runs unconditionally — those are real platform reference data,
/// not demo fixtures, and must exist in every environment.
/// </summary>
public sealed class BusinessBootstrapOptions
{
    public const string SectionName = "BusinessBootstrap";

    public BootstrapFixturesOptions Fixtures { get; set; } = new();

    public sealed class BootstrapFixturesOptions
    {
        public bool Enabled { get; set; } = true;

        public bool AllowInNonDevelopment { get; set; }

        public bool IsActive(bool isDevelopment) =>
            Enabled && (isDevelopment || AllowInNonDevelopment);
    }
}
