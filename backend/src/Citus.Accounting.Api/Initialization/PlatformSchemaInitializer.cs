using Citus.Platform.Core.Abstractions;

namespace Citus.Accounting.Api.Initialization;

/// <summary>
/// Idempotent platform-tables initializer.
///
/// Runs unconditionally in every environment, because the Accounting API's
/// account / tax-code / journal-entry / FX inserts FK into
/// <c>currency_catalog</c>, <c>companies</c>, <c>users</c>,
/// <c>company_memberships</c>. Without these tables (and the ISO 4217
/// currency rows), every business write fails with 23503. Calls
/// <see cref="IPlatformFirstCompanyProvisioningRepository.EnsureSchemaAsync"/>,
/// which is idempotent — every CREATE TABLE / INSERT in that path uses
/// IF NOT EXISTS / ON CONFLICT DO NOTHING.
/// </summary>
public sealed class PlatformSchemaInitializer
{
    private readonly IPlatformFirstCompanyProvisioningRepository _provisioningRepository;

    public PlatformSchemaInitializer(IPlatformFirstCompanyProvisioningRepository provisioningRepository)
    {
        _provisioningRepository = provisioningRepository;
    }

    public Task EnsureAsync(CancellationToken cancellationToken) =>
        _provisioningRepository.EnsureSchemaAsync(cancellationToken);
}
