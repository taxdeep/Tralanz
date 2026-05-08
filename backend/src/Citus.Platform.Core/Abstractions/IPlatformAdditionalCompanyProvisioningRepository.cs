using Citus.Platform.Core.Runtime;

namespace Citus.Platform.Core.Abstractions;

/// <summary>
/// Provisions a SECOND-or-later company for an already-authenticated
/// business user. Distinct from <see cref="IPlatformFirstCompanyProvisioningRepository"/>
/// because:
///
///  * the calling user already exists — no owner credentials are
///    collected and no new <c>users</c> row is created;
///  * the platform-wide guard "no companies have been provisioned yet"
///    is irrelevant here — by definition this path only fires once at
///    least one company already exists;
///  * the heavy <c>lock table users, companies, company_memberships in
///    access exclusive mode</c> the first-company path uses to avoid
///    the bootstrap race is overkill on a populated platform — the
///    Nth-company path relies on per-row UPSERT contention only.
///
/// Implementations share most of their code with the first-company
/// repository (chart-of-accounts seeding, primary book, currency
/// enablement, …) — see <c>PostgresPlatformFirstCompanyProvisioningRepository</c>.
/// </summary>
public interface IPlatformAdditionalCompanyProvisioningRepository
{
    Task<PlatformAdditionalCompanyProvisioningResult> ProvisionAsync(
        PlatformAdditionalCompanyProvisioningCommand command,
        CancellationToken cancellationToken);
}
