using Modules.CompanyAccess.Permissions;

namespace Infrastructure.PostgreSQL.CompanyAccess;

/// <summary>
/// PostgreSQL-backed <see cref="IPermissionEvaluator"/>. Every method
/// is a small targeted SELECT against the foundation tables created
/// in the 2026-05-19-permission-foundation migration.
///
/// No caching in this layer — keep authorization decisions consistent
/// with the latest committed grant state. The expected per-request
/// pattern is: API filter (or service) invokes one or two checks,
/// each adding ≤1 round-trip to PG. Connection-pool size, not query
/// count, dominates throughput.
///
/// Future optimisation (deferred): cache the (companyId, userId) →
/// is_owner / is_active_member tuple for the lifetime of a single
/// request via a scoped accessor.
/// </summary>
public sealed class PostgreSqlPermissionEvaluator(PostgreSqlConnectionFactory connections)
    : IPermissionEvaluator
{
    public async Task<bool> IsActiveMemberAsync(
        CompanyId companyId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(companyId.Value) || string.IsNullOrEmpty(userId.Value))
        {
            return false;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists(
              select 1
                from company_memberships
               where company_id = @company_id
                 and user_id = @user_id
                 and status = 'active'
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("user_id", userId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> IsOwnerAsync(
        CompanyId companyId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(companyId.Value) || string.IsNullOrEmpty(userId.Value))
        {
            return false;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists(
              select 1
                from company_memberships
               where company_id = @company_id
                 and user_id = @user_id
                 and is_owner = true
                 and status = 'active'
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("user_id", userId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> CanPerformOwnerOnlyActionAsync(
        CompanyId companyId,
        UserId actorId,
        string ownerOnlyAction,
        CancellationToken cancellationToken)
    {
        // Reject anything not in the catalogued set — even if the
        // operator passes a typo'd or made-up action. Owner-only is
        // a closed list maintained alongside the registry seed.
        if (!OwnerOnlyActions.IsOwnerOnly(ownerOnlyAction))
        {
            return false;
        }

        return await IsOwnerAsync(companyId, actorId, cancellationToken);
    }

    public async Task<bool> CanAsync(
        CompanyId companyId,
        UserId actorId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(permissionToken))
        {
            return false;
        }

        // Owner-only tokens are never granted via the business path.
        // Callers asking "can the user post an invoice" use this method
        // with 'ar.invoice.post'; callers asking "can the user
        // transfer ownership" must use CanPerformOwnerOnlyActionAsync.
        // Returning false here keeps the two paths from accidentally
        // coalescing.
        if (OwnerOnlyActions.IsOwnerOnly(permissionToken))
        {
            return false;
        }

        // Owner bypass — implied-all-permissions inside the company.
        // We check Owner first so the explicit-grant query is skipped
        // entirely for Owners (one round-trip instead of two).
        if (await IsOwnerAsync(companyId, actorId, cancellationToken))
        {
            return true;
        }

        // Explicit grant. Joins to permission_registry so a token that
        // was somehow grant-rowed but is currently non-assignable
        // (e.g. Owner-only got mis-seeded) is excluded.
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists(
              select 1
                from company_user_permissions cup
                join company_memberships m
                  on m.company_id = cup.company_id
                 and m.user_id = cup.user_id
                join permission_registry r
                  on r.permission_token = cup.permission_token
               where cup.company_id = @company_id
                 and cup.user_id = @user_id
                 and cup.permission_token = @token
                 and cup.is_active = true
                 and m.status = 'active'
                 and r.is_assignable = true
            );
            """;
        command.Parameters.AddWithValue("company_id", companyId.Value);
        command.Parameters.AddWithValue("user_id", actorId.Value);
        command.Parameters.AddWithValue("token", permissionToken);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<GrantAuthorityResult> CanGrantAsync(
        CompanyId companyId,
        UserId actorId,
        UserId targetId,
        string permissionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(permissionToken))
        {
            return GrantAuthorityResult.DeniedTokenNotInRegistry;
        }

        // Owner-only actions can never be granted via this path —
        // they're hard-coded to is_owner=true and have is_assignable=
        // false in the registry. Also covers the explicit
        // anti-recursion case: permission_grant_authority.{assign,
        // revoke} are Owner-only, so a User with grant authority over
        // ap.bill.view cannot use that authority to delegate further.
        if (OwnerOnlyActions.IsOwnerOnly(permissionToken))
        {
            return GrantAuthorityResult.DeniedTokenNotAssignable;
        }

        // Self-grant blocked unconditionally in v1. Owner already has
        // implied-all so there's no legitimate use; for non-Owner this
        // would be self-promotion.
        if (string.Equals(actorId.Value, targetId.Value, StringComparison.Ordinal))
        {
            return GrantAuthorityResult.DeniedSelfGrant;
        }

        // Both actor and target must be active members of this
        // company. We check actor first so callers get the most
        // actionable error.
        if (!await IsActiveMemberAsync(companyId, actorId, cancellationToken))
        {
            return GrantAuthorityResult.DeniedActorNotActiveMember;
        }

        if (!await IsActiveMemberAsync(companyId, targetId, cancellationToken))
        {
            return GrantAuthorityResult.DeniedTargetNotActiveMember;
        }

        // Target cannot be Owner. Owner's permissions are implied-all
        // and immutable from the grant path; only an
        // owner.transfer flips ownership.
        if (await IsOwnerAsync(companyId, targetId, cancellationToken))
        {
            return GrantAuthorityResult.DeniedTargetIsOwner;
        }

        // Token must exist and be assignable.
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using (var registryCommand = connection.CreateCommand())
        {
            registryCommand.CommandText =
                "select is_assignable from permission_registry where permission_token = @token;";
            registryCommand.Parameters.AddWithValue("token", permissionToken);
            var assignable = await registryCommand.ExecuteScalarAsync(cancellationToken);
            if (assignable is null)
            {
                return GrantAuthorityResult.DeniedTokenNotInRegistry;
            }

            if (!(bool)assignable)
            {
                return GrantAuthorityResult.DeniedTokenNotAssignable;
            }
        }

        // Owner bypass — Owner can grant any assignable token. We
        // checked Owner-on-target above, so the Owner here is the
        // actor.
        if (await IsOwnerAsync(companyId, actorId, cancellationToken))
        {
            return GrantAuthorityResult.Allowed;
        }

        // Non-Owner path: actor must have an active grant-authority
        // row for this exact token, with can_grant=true.
        await using var authorityCommand = connection.CreateCommand();
        authorityCommand.CommandText =
            """
            select exists(
              select 1
                from company_user_permission_grant_authorities
               where company_id = @company_id
                 and user_id = @actor_id
                 and grantable_permission_token = @token
                 and is_active = true
                 and can_grant = true
            );
            """;
        authorityCommand.Parameters.AddWithValue("company_id", companyId.Value);
        authorityCommand.Parameters.AddWithValue("actor_id", actorId.Value);
        authorityCommand.Parameters.AddWithValue("token", permissionToken);
        var hasAuthority = (bool)(await authorityCommand.ExecuteScalarAsync(cancellationToken) ?? false);

        return hasAuthority
            ? GrantAuthorityResult.Allowed
            : GrantAuthorityResult.DeniedActorMissingGrantAuthority;
    }
}
