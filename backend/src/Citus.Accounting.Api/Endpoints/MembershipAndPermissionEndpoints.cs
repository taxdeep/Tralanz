using Citus.Accounting.Api;
using Citus.Accounting.Api.Endpoints;
using Citus.Accounting.Api.Startup;
using static Citus.Accounting.Api.AccountingEndpointHelpers;
using static Citus.Accounting.Api.CompanyCurrencyResponseMapper;
using static Citus.Accounting.Api.InventoryItemRequestMapper;
using static Citus.Accounting.Api.Authorization.EndpointApprovalAuthorityHelpers;
using static Citus.Accounting.Api.Endpoints.Support.ReviewMappers;
using static Citus.Accounting.Api.Endpoints.Support.BusinessSessionEndpointHelpers;
using static Citus.Accounting.Api.Endpoints.Support.EndpointRequestHelpers;
using Citus.Accounting.Api.Initialization;
using Citus.Accounting.Api.Tasks;
using Citus.Accounting.Application;
using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Application.CoaTemplates;
using Citus.Accounting.Application.Commands;
using Citus.Accounting.Application.Companies;
using Citus.Accounting.Application.Invoices;
using Citus.Accounting.Application.Queries;
using Citus.Accounting.Application.Statements;
using Citus.Accounting.Application.Reconciliation;
using Citus.Accounting.Application.Repositories;
using Citus.Accounting.Domain.Common;
using Citus.Accounting.Domain.Documents;
using Citus.Platform.Core.Abstractions;
using Citus.Platform.Infrastructure.Persistence;
using Citus.Ui.Shared.Business;
using Citus.Ui.Shared.Journal;
using Citus.Ui.Shared.Reports;
using Citus.Ui.Shared.Shell;
using Citus.Accounting.Infrastructure.Companies;
using Citus.Accounting.Infrastructure.Fx;
using Citus.Accounting.Infrastructure.Invoices;
using Citus.Accounting.Infrastructure.Persistence;
using Citus.Accounting.Infrastructure.Posting;
using Citus.Accounting.Infrastructure.Statements;
using Citus.Modules.UnitySearch.Application;
using Citus.Modules.UnitySearch.Application.Contracts;
using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Citus.Modules.Inventory.Application.Contracts;
using Citus.Modules.Inventory.Application.Contracts.Pricing;
using Citus.Modules.Inventory.Application.Pricing;
using Citus.Modules.Inventory.Domain.Shared;
using Citus.Modules.Inventory.Domain.Shared.Pricing;
using Citus.Modules.Tasks.Application;
using Citus.Modules.Tasks.Application.Contracts;
using Citus.Modules.Tasks.Domain.Shared;
using Citus.Modules.Tasks.Domain.Shared.Reports;
using TaskStatus = Citus.Modules.Tasks.Domain.Shared.TaskStatus;
using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.Accounts;
using Infrastructure.PostgreSQL.Uom;
using Infrastructure.PostgreSQL.BusinessAuth;
using Infrastructure.PostgreSQL.Banking;
using Infrastructure.PostgreSQL.Company;
using Infrastructure.PostgreSQL.CompanyAccess;
using Infrastructure.PostgreSQL.AP.Bills;
using Infrastructure.PostgreSQL.AP.Expenses;
using Infrastructure.PostgreSQL.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.Counterparties;
using Infrastructure.PostgreSQL.Sales;
using Modules.AP.Bills;
using Modules.AP.Expenses;
using Modules.AP.PurchaseOrders;
using Infrastructure.PostgreSQL.GL;
using Infrastructure.PostgreSQL.Inventory;
using Infrastructure.PostgreSQL.Inventory.Posting;
using Infrastructure.PostgreSQL.Numbering;
using Infrastructure.PostgreSQL.Tax;
using Infrastructure.PostgreSQL.UnitySearch;
using Infrastructure.PostgreSQL.UnityAi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Citus.Accounting.Api.Authorization;
using Modules.CompanyAccess.Memberships;
using Modules.CompanyAccess.SessionContext;
using Npgsql;
using Modules.Company.FeatureManagement;
using Modules.Company.MultiBook;
using Modules.Company.MultiCurrency;
using System.Text;
using System.Threading.RateLimiting;
using JournalEntryNumberLookup = Engines.Numbering.JournalEntry.IJournalEntryNumberLookup;
using GlIJournalEntryLifecycleStore = Modules.GL.JournalEntry.IJournalEntryLifecycleStore;
using GlIJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.IJournalEntryLifecycleWorkflow;
using GlJournalEntryLifecycleWorkflow = Modules.GL.JournalEntry.JournalEntryLifecycleWorkflow;

namespace Citus.Accounting.Api.Endpoints;

/// <summary>
/// MembershipAndPermission endpoints extracted verbatim from Program.cs (P6). Registered on the
/// same /accounting group instance, so the business-session/maintenance guard
/// and every per-route permission/rate-limit filter are preserved unchanged.
/// </summary>
internal static class MembershipAndPermissionEndpoints
{
    public static void MapMembershipAndPermissionEndpoints(this RouteGroupBuilder accounting)
    {

        // CreditMemo + VendorCredit reuse the existing credit_notes /
        // vendor_credits detail endpoints which already shipped in main.
        // Frontend's CreditMemoClient / VendorCreditClient just call those.

        // =====================================================================
        // PR-4E: Permission management endpoints.
        //
        // Three endpoints for the Tralanz permission model write path:
        //   GET    /accounting/memberships/{userId}/permissions  — snapshot
        //   POST   /accounting/memberships/{userId}/permissions/grant
        //   POST   /accounting/memberships/{userId}/permissions/revoke
        //
        // All three accept the target user_id (char(7), e.g. "U000001") in
        // the URL. Company context comes from the request body's CompanyId
        // (validated by BusinessRequestContractGuard against the session
        // active company). Actor comes from BusinessSessionContextAccessor.
        //
        // Authorization:
        //   * GET:  caller must be the Owner of this company OR the target
        //           user themselves. Anything else is 403. Non-Owner users
        //           who want to inspect someone else's grants must ask Owner.
        //   * POST: workflow validates via IPermissionEvaluator.CanGrantAsync
        //           which encodes all eight hard rules (target not Owner,
        //           not self-grant, token assignable, actor has authority,
        //           etc.). Returns 403 on denial with the precise rejection
        //           code; UI can render the reason verbatim.
        //
        // Audit: every successful grant/revoke writes an audit_logs row with
        // actor_type='business_user', action='permission_granted' /
        // 'permission_revoked', and the full triple in payload.
        // =====================================================================

        // =====================================================================
        // PR-4F: Owner-only read endpoints powering the permission management UI.
        // =====================================================================

        accounting.MapGet(
            "/memberships",
            async (
                [AsParameters] V1PendingLookupQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                global::Modules.CompanyAccess.Permissions.IPermissionEvaluator evaluator,
                PostgreSqlConnectionFactory connections,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null) return Results.Unauthorized();

                // Owner-only for v1. Non-Owners managing permissions of others
                // is a follow-up once delegated grant-authority UX lands.
                if (!await evaluator.IsOwnerAsync(query.CompanyId, session.UserId, cancellationToken))
                {
                    return Results.Problem(
                        title: "Forbidden.",
                        detail: "Only the company owner can list company members.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var rows = new List<object>();
                await using var connection = await connections.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    select m.user_id, m.is_owner, m.is_active, m.status,
                           u.email, coalesce(u.display_name, '') as display_name,
                           coalesce(u.username, '') as username
                      from company_memberships m
                      join users u on u.id = m.user_id
                     where m.company_id = @company_id
                     order by m.is_owner desc, u.display_name asc, u.email asc;
                    """;
                command.Parameters.AddWithValue("company_id", query.CompanyId.Value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new
                    {
                        UserId = reader.GetString(0),
                        IsOwner = reader.GetBoolean(1),
                        IsActive = reader.GetBoolean(2),
                        Status = reader.GetString(3),
                        Email = reader.GetString(4),
                        DisplayName = reader.GetString(5),
                        Username = reader.GetString(6),
                    });
                }
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/permissions/registry",
            async (
                BusinessSessionContextAccessor sessionAccessor,
                PostgreSqlConnectionFactory connections,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null) return Results.Unauthorized();

                // Registry is shared across companies — no company filter
                // needed. Any active session can read it (the UI uses it to
                // render token labels even on read-only screens).
                var rows = new List<object>();
                await using var connection = await connections.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    select permission_token, module_key, group_key, action_key,
                           description, is_high_risk, is_assignable
                      from permission_registry
                     where is_assignable = true
                     order by module_key asc, group_key asc, action_key asc;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new
                    {
                        PermissionToken = reader.GetString(0),
                        ModuleKey = reader.GetString(1),
                        GroupKey = reader.GetString(2),
                        ActionKey = reader.GetString(3),
                        Description = reader.GetString(4),
                        IsHighRisk = reader.GetBoolean(5),
                        IsAssignable = reader.GetBoolean(6),
                    });
                }
                return Results.Ok(rows);
            });

        accounting.MapGet(
            "/memberships/{userId}/permissions",
            async (
                string userId,
                [AsParameters] V1PendingLookupQuery query,
                BusinessSessionContextAccessor sessionAccessor,
                global::Modules.CompanyAccess.Permissions.IPermissionEvaluator evaluator,
                global::Modules.CompanyAccess.Permissions.IPermissionGrantWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null) return Results.Unauthorized();

                if (!UserId.TryParse(userId, out var targetId))
                {
                    return Results.BadRequest(new { message = "Invalid user id." });
                }

                // Caller can read their own permissions; Owner can read
                // anyone's. Non-Owner reading someone else's grants is 403.
                var isSelf = string.Equals(session.UserId.Value, targetId.Value, StringComparison.Ordinal);
                var isOwner = await evaluator.IsOwnerAsync(query.CompanyId, session.UserId, cancellationToken);
                if (!isSelf && !isOwner)
                {
                    return Results.Problem(
                        title: "Forbidden.",
                        detail: "Only the company owner or the user themselves can read this permission snapshot.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var snapshot = await workflow.GetUserPermissionsAsync(query.CompanyId, targetId, cancellationToken);
                return Results.Ok(new
                {
                    CompanyId = snapshot.CompanyId,
                    UserId = snapshot.UserId,
                    snapshot.IsOwner,
                    ActiveGrants = snapshot.ActiveGrants.Select(g => new
                    {
                        g.PermissionToken,
                        GrantedByUserId = g.GrantedByUserId,
                        g.GrantedAtUtc,
                        g.IsActive,
                    }),
                    ActiveGrantAuthorities = snapshot.ActiveGrantAuthorities.Select(a => new
                    {
                        a.GrantablePermissionToken,
                        a.CanGrant,
                        a.CanRevoke,
                        GrantedByOwnerUserId = a.GrantedByOwnerUserId,
                        a.GrantedAtUtc,
                        a.IsActive,
                    }),
                });
            });

        accounting.MapPost(
            "/memberships/{userId}/permissions/grant",
            async (
                string userId,
                PermissionMutationHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                global::Modules.CompanyAccess.Permissions.IPermissionGrantWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null) return Results.Unauthorized();

                if (!UserId.TryParse(userId, out var targetId))
                {
                    return Results.BadRequest(new { message = "Invalid user id." });
                }

                var result = await workflow.GrantAsync(
                    request.CompanyId,
                    session.UserId,
                    targetId,
                    request.PermissionToken,
                    cancellationToken);

                if (result.ResultCode != global::Modules.CompanyAccess.Permissions.GrantAuthorityResult.Allowed)
                {
                    return Results.Problem(
                        title: "Permission grant rejected.",
                        detail: result.ResultMessage,
                        statusCode: StatusCodes.Status403Forbidden,
                        extensions: new Dictionary<string, object?>
                        {
                            ["resultCode"] = result.ResultCode.ToString(),
                        });
                }

                return Results.Ok(new
                {
                    CompanyId = result.CompanyId,
                    ActorUserId = result.ActorUserId,
                    TargetUserId = result.TargetUserId,
                    result.PermissionToken,
                    result.Action,
                    result.Applied,
                    ResultCode = result.ResultCode.ToString(),
                    result.ResultMessage,
                });
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsPermissionsAssign);

        accounting.MapPost(
            "/memberships/{userId}/permissions/revoke",
            async (
                string userId,
                PermissionMutationHttpRequest request,
                BusinessSessionContextAccessor sessionAccessor,
                global::Modules.CompanyAccess.Permissions.IPermissionGrantWorkflow workflow,
                CancellationToken cancellationToken) =>
            {
                var session = sessionAccessor.Current;
                if (session is null) return Results.Unauthorized();

                if (!UserId.TryParse(userId, out var targetId))
                {
                    return Results.BadRequest(new { message = "Invalid user id." });
                }

                var result = await workflow.RevokeAsync(
                    request.CompanyId,
                    session.UserId,
                    targetId,
                    request.PermissionToken,
                    cancellationToken);

                if (result.ResultCode != global::Modules.CompanyAccess.Permissions.GrantAuthorityResult.Allowed)
                {
                    return Results.Problem(
                        title: "Permission revoke rejected.",
                        detail: result.ResultMessage,
                        statusCode: StatusCodes.Status403Forbidden,
                        extensions: new Dictionary<string, object?>
                        {
                            ["resultCode"] = result.ResultCode.ToString(),
                        });
                }

                return Results.Ok(new
                {
                    CompanyId = result.CompanyId,
                    ActorUserId = result.ActorUserId,
                    TargetUserId = result.TargetUserId,
                    result.PermissionToken,
                    result.Action,
                    result.Applied,
                    ResultCode = result.ResultCode.ToString(),
                    result.ResultMessage,
                });
            }).RequireGrantedPermission(CompanyMembershipPermissionCatalog.SettingsPermissionsAssign);
    }
}
