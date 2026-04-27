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
    Task<CoaSeedSummary> SeedAsync(
        Guid companyId,
        string templateKey,
        CancellationToken cancellationToken);
}
