using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Domain.Periods;

/// <summary>
/// M7: per-company accounting period. State machine drives the
/// effective-date validation that lands in iter 2.
///
///   open    → posting allowed for any user, any effective date in range
///   closing → admin-only posting, 7-day grace before close (V1: handled
///             by the iter 2 effective-date validator, not by
///             time-based auto-transition)
///   closed  → blocked. Reversals create offsetting posts in the
///             current open period (operator's job; the validator just
///             refuses backdated entries).
///   locked  → audit-only. Even reversals forbidden.
///
/// Periods are monthly in V1; quarter / 4-4-5 patterns are deferred
/// to the ERP tier. Period end-of-month is computed from the company's
/// fiscal_year_end_month / fiscal_year_end_day.
/// </summary>
public sealed record AccountingPeriod(
    Guid Id,
    CompanyId CompanyId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    DateTimeOffset? ClosingStartedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? LockedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class AccountingPeriodStatus
{
    public const string Open = "open";
    public const string Closing = "closing";
    public const string Closed = "closed";
    public const string Locked = "locked";

    public static readonly IReadOnlyList<string> All =
        [Open, Closing, Closed, Locked];

    /// <summary>
    /// Allowed forward state transitions. Reversals (closing → open,
    /// closed → open) are not in V1 — re-opening a closed period
    /// would invalidate the matching-principle guarantee for any
    /// reports already published.
    /// </summary>
    public static bool IsAllowedTransition(string from, string to) =>
        (from, to) switch
        {
            (Open, Closing) => true,
            (Closing, Closed) => true,
            (Closed, Locked) => true,
            _ => false,
        };
}
