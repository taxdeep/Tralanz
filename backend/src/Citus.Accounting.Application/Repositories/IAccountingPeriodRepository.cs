using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Periods;

namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// M7: read + transition for per-company accounting periods. The
/// repository is also responsible for lazy-seeding monthly periods
/// for the company's current fiscal year on first read — operators
/// don't have to provision them manually.
/// </summary>
public interface IAccountingPeriodRepository
{
    /// <summary>
    /// List all periods for the company in chronological order.
    /// First call also seeds the company's current fiscal year of
    /// monthly periods (all status='open') if none exist yet.
    /// </summary>
    Task<IReadOnlyList<AccountingPeriod>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Apply a state transition with audit log. Throws if the
    /// transition is not allowed (per
    /// <see cref="AccountingPeriodStatus.IsAllowedTransition"/>) or
    /// if the period does not belong to the company.
    /// </summary>
    Task<AccountingPeriod> TransitionAsync(
        CompanyId companyId,
        UserId actorUserId,
        Guid periodId,
        string targetStatus,
        CancellationToken cancellationToken);
}
