// BusinessSessionContextAccessor lives in the Citus.Accounting.Api
// namespace; pull it in explicitly so the filter compiles without a
// fully-qualified type name in the body.
using Citus.Accounting.Api;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.Permissions;

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

    /// <summary>
    /// PR-4C entry point for the new Tralanz permission model.
    /// Authorizes the request via <see cref="IPermissionEvaluator"/>
    /// rather than the legacy <c>session.Roles</c> jsonb cache:
    /// Owner bypasses (implied-all); non-Owner needs an active row in
    /// <c>company_user_permissions</c> for the token, with
    /// <c>is_assignable=true</c> in <c>permission_registry</c>.
    ///
    /// Use this for any new high-risk endpoint gate. The legacy
    /// <see cref="RequirePermission{TBuilder}"/> remains for Task /
    /// inventory-pricing endpoints whose tests still seed via the old
    /// jsonb column; those will migrate in a later sweep PR.
    ///
    /// Status code convention identical to <c>RequirePermission</c>:
    /// no session → 401, session present but unauthorized → 403.
    /// </summary>
    public static TBuilder RequireGrantedPermission<TBuilder>(this TBuilder builder, string permissionToken)
        where TBuilder : IEndpointConventionBuilder
    {
        if (string.IsNullOrWhiteSpace(permissionToken))
        {
            throw new ArgumentException("Permission token is required.", nameof(permissionToken));
        }

        // Normalize once at startup so per-request work is just the
        // DB check. Throws on unknown tokens — programmer error.
        var normalized = CompanyMembershipPermissionCatalog.NormalizeTokens(new[] { permissionToken })[0];

        builder.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.RequestServices
                .GetService<BusinessSessionContextAccessor>()?.Current;
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var evaluator = context.HttpContext.RequestServices
                .GetRequiredService<IPermissionEvaluator>();

            var allowed = await evaluator.CanAsync(
                session.ActiveCompanyId,
                session.UserId,
                normalized,
                context.HttpContext.RequestAborted);

            if (!allowed)
            {
                return Results.Problem(
                    title: "Missing required permission.",
                    detail: $"This operation requires permission '{normalized}'. Ask the company owner to grant it.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });

        return builder;
    }

    /// <summary>
    /// Gate an endpoint on one of the four Owner-only hard-coded
    /// actions (see <see cref="OwnerOnlyActions"/>). These are NEVER
    /// delegatable — even a User with grant authority over every
    /// other token cannot perform them. Use for
    /// <c>company.make_inactive</c>, <c>owner.transfer</c>,
    /// <c>permission_grant_authority.{assign,revoke}</c>.
    /// </summary>
    public static TBuilder RequireOwnerOnlyAction<TBuilder>(this TBuilder builder, string ownerOnlyAction)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!OwnerOnlyActions.IsOwnerOnly(ownerOnlyAction))
        {
            throw new ArgumentException(
                $"'{ownerOnlyAction}' is not one of the catalogued Owner-only actions.",
                nameof(ownerOnlyAction));
        }

        builder.AddEndpointFilter(async (context, next) =>
        {
            var session = context.HttpContext.RequestServices
                .GetService<BusinessSessionContextAccessor>()?.Current;
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var evaluator = context.HttpContext.RequestServices
                .GetRequiredService<IPermissionEvaluator>();

            var allowed = await evaluator.CanPerformOwnerOnlyActionAsync(
                session.ActiveCompanyId,
                session.UserId,
                ownerOnlyAction,
                context.HttpContext.RequestAborted);

            if (!allowed)
            {
                return Results.Problem(
                    title: "Owner-only action.",
                    detail: $"Only the company owner can perform '{ownerOnlyAction}'.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });

        return builder;
    }
}
