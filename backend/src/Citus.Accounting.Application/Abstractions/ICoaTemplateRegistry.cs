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
    Task<CoaSeedSummary> SeedAsync(
        CompanyId companyId,
        string templateKey,
        CancellationToken cancellationToken,
        bool additive = false);
}
