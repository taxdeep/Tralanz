using Citus.Accounting.Application.CoaTemplates;

namespace Citus.Accounting.Application.Abstractions;

/// <summary>
/// Read-only catalog of chart-of-accounts starter templates. V1 ships
/// the templates as static C# data via
/// <see cref="StaticCoaTemplateRegistry"/>; a future revision may load
/// them from disk or a registry table. The contract stays the same.
/// </summary>
public interface ICoaTemplateRegistry
{
    IReadOnlyList<CoaTemplate> List();

    CoaTemplate? Get(string templateKey);
}

/// <summary>
/// Applies a template to a target company. Idempotent — re-applying the
/// same template skips rows whose <c>code</c> already exists, so callers
/// can safely retry after partial failures.
/// </summary>
public interface ICoaTemplateSeeder
{
    /// <param name="additive">
    /// When <c>false</c> (default) the call is rejected if the company
    /// already has any accounts — used by first-company provisioning and
    /// the user-driven "apply template" action so an operator can't
    /// accidentally re-introduce system rows after curating their chart.
    /// When <c>true</c> the upfront empty-chart guard is bypassed and
    /// missing rows are filled in row-by-row (existing rows are still
    /// skipped — that idempotency lives in the per-row API). Used by
    /// post-onboarding flows that need to ensure a known set of system
    /// accounts exists, like Inventory module activation.
    /// </param>
    /// <param name="accountCodeLength">
    /// When provided, canonical 5-digit template codes are scaled to this
    /// width before insert: codes longer than 5 right-pad with zeros
    /// (<c>14000</c> → <c>1400000</c> at 7 digits), shorter widths drop
    /// zero tails. Rows whose tail is non-zero (e.g. <c>13701</c>) are
    /// skipped under widths smaller than 5 — same rule first-company
    /// provisioning uses. Without this argument the literal canonical
    /// code is inserted, which is what the user-driven "apply template"
    /// action wants on a fresh chart.
    /// </param>
    Task<CoaSeedSummary> SeedAsync(
        CompanyId companyId,
        string templateKey,
        CancellationToken cancellationToken,
        bool additive = false,
        int? accountCodeLength = null);
}
