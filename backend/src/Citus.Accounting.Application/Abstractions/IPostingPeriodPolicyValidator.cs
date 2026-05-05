using Citus.Accounting.Domain.Common;

namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// M7 iter 2: enforces accounting-period state on the effective date
/// of every JE the engine posts. The check runs at the engine layer
/// so every posting path (Invoice, Bill, Receipt, COGS hooks, deposit
/// applications, drop-ship clearing, manual JEs, …) inherits it
/// without each handler re-implementing the rule.
///
/// V1 policy:
/// - effective date in an <c>open</c> period       → allowed
/// - effective date in a <c>closing</c> period     → allowed
///     (the plan's admin-only-during-closing rule
///      lands in V2 once role context flows into
///      <see cref="Citus.Accounting.Domain.Posting.PostingContext"/>)
/// - effective date in a <c>closed</c> period      → blocked
/// - effective date in a <c>locked</c> period      → blocked
/// - effective date with no matching period        → allowed
///     (V1 lazy-seeds the current fiscal year only;
///      historical / future dates outside that window
///      are permitted unless the operator manually
///      seeds the period as closed)
/// </summary>
public interface IPostingPeriodPolicyValidator
{
    Task ValidateAsync(
        CompanyId companyId,
        DateOnly effectiveDate,
        string sourceType,
        CancellationToken cancellationToken);
}
