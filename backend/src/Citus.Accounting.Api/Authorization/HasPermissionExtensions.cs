// BusinessSessionContextAccessor lives in the Citus.Accounting.Api
// namespace; pull it in explicitly so the filter compiles without a
// fully-qualified type name in the body.
using Citus.Accounting.Api;
using Modules.CompanyAccess.Memberships;

namespace Citus.Accounting.Api.Authorization;

/// <summary>
/// Endpoint-filter helpers that gate a minimal-API endpoint on the
/// permission tokens the calling user holds in the active company.
///
/// Wire it on the route handler:
///
/// <code>
/// accounting.MapPost("/tasks", ...).RequirePermission("task.create");
/// </code>
///
/// or on a MapGroup:
///
/// <code>
/// var tasks = app.MapGroup("/accounting/tasks").RequireAnyPermission("task.view", "task.view.all");
/// </code>
///
/// Status code convention:
/// <list type="bullet">
///   <item>No session at all → <see cref="StatusCodes.Status401Unauthorized"/>.</item>
///   <item>Session present but token missing → <see cref="StatusCodes.Status403Forbidden"/>.</item>
/// </list>
/// 403 here, not 404 — the resource <i>exists</i> for this company,
/// the caller simply lacks the required permission. (Contrast with
/// <c>RequireModuleEnabled</c>, which returns 404 because the module
/// genuinely is not present for the company.)
///
/// All passed tokens are validated against
/// <see cref="CompanyMembershipPermissionCatalog"/> at startup time
/// (via wiring code) — passing an unknown token here is a programmer
/// error and will throw on first request.
/// </summary>
public static class HasPermissionExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionToken)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAnyPermission(permissionToken);

    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params string[] permissionTokens)
        where TBuilder : IEndpointConventionBuilder
    {
        if (permissionTokens is null || permissionTokens.Length == 0)
        {
            throw new ArgumentException("At least one permission token is required.", nameof(permissionTokens));
        }

        var normalized = CompanyMembershipPermissionCatalog.NormalizeTokens(permissionTokens);

        builder.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.RequestServices
                .GetService<BusinessSessionContextAccessor>()?.Current;
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var held = session.Roles;
            foreach (var token in normalized)
            {
                if (held.Contains(token, StringComparer.Ordinal))
                {
                    return await next(context);
                }
            }

            return Results.Problem(
                title: "Missing required permission.",
                detail: $"This operation requires one of: {string.Join(", ", normalized)}.",
                statusCode: StatusCodes.Status403Forbidden);
        });

        return builder;
    }

    public static TBuilder RequireAllPermissions<TBuilder>(this TBuilder builder, params string[] permissionTokens)
        where TBuilder : IEndpointConventionBuilder
    {
        if (permissionTokens is null || permissionTokens.Length == 0)
        {
            throw new ArgumentException("At least one permission token is required.", nameof(permissionTokens));
        }

        var normalized = CompanyMembershipPermissionCatalog.NormalizeTokens(permissionTokens);

        builder.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.RequestServices
                .GetService<BusinessSessionContextAccessor>()?.Current;
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var held = new HashSet<string>(session.Roles, StringComparer.Ordinal);
            foreach (var token in normalized)
            {
                if (!held.Contains(token))
                {
                    return Results.Problem(
                        title: "Missing required permission.",
                        detail: $"This operation requires all of: {string.Join(", ", normalized)}.",
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            return await next(context);
        });

        return builder;
    }
}
