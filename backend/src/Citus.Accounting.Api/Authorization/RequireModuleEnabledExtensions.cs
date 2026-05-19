// BusinessSessionContextAccessor lives in the Citus.Accounting.Api
// namespace; pull it in explicitly so the filter compiles without a
// fully-qualified type name in the body.
using Citus.Accounting.Api;
using Modules.Company.FeatureManagement;

namespace Citus.Accounting.Api.Authorization;

/// <summary>
/// Endpoint-filter helpers that gate a minimal-API endpoint on a
/// per-company module flag.
///
/// Wire it on the route handler:
///
/// <code>
/// accounting.MapGet("/tasks", ...).RequireModuleEnabled("task");
/// </code>
///
/// or on a whole MapGroup:
///
/// <code>
/// var tasks = app.MapGroup("/accounting/tasks").RequireModuleEnabled("task");
/// </code>
///
/// Behavior when the flag is off: returns <see cref="StatusCodes.Status404NotFound"/>
/// (not 403). 404 is intentional — a 403 leaks "this company has a task
/// module but you can't touch it", which is a finger-pointing signal.
/// 404 says the endpoint simply isn't there for this company.
///
/// Unknown module keys (catalog rejects them) also surface as 404 from
/// the cached workflow path — the gate is fail-closed.
/// </summary>
public static class RequireModuleEnabledExtensions
{
    public static TBuilder RequireModuleEnabled<TBuilder>(this TBuilder builder, string moduleKey)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        var normalized = moduleKey.Trim().ToLowerInvariant();

        builder.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.RequestServices
                .GetService<BusinessSessionContextAccessor>()?.Current;
            if (session is null || string.IsNullOrEmpty(session.ActiveCompanyId.Value))
            {
                return Results.Unauthorized();
            }

            var workflow = context.HttpContext.RequestServices
                .GetRequiredService<ICompanyModuleFlagWorkflow>();
            var enabled = await workflow.IsEnabledAsync(
                session.ActiveCompanyId,
                normalized,
                context.HttpContext.RequestAborted);

            if (!enabled)
            {
                return Results.NotFound();
            }

            return await next(context);
        });

        return builder;
    }
}
