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

namespace Citus.Accounting.Api.Authentication;

/// <summary>
/// Business sign-in / session endpoints extracted verbatim from Program.cs
/// (P7). Mounted at app root (NOT under /accounting) because the Blazor client
/// posts to auth/login|session|logout relative to the API base. Auth model,
/// token handling, and rate limiting are unchanged.
/// </summary>
public static class AuthenticationEndpoints
{
    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        app.MapPost(
            "/auth/login",
            async (
                BusinessSignInRequest request,
                HttpContext httpContext,
                Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
                global::Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
                IConfiguration configuration,
                CancellationToken cancellationToken) =>
            {
                var sessionLifetime = ResolveBusinessSessionLifetime(configuration);
                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
                var userAgent = httpContext.Request.Headers.UserAgent.ToString();

                var result = await repository.AuthenticateAsync(
                    request.Email,
                    request.Password,
                    sessionLifetime,
                    remoteIp,
                    userAgent,
                    cancellationToken);

                if (!result.Succeeded)
                {
                    return Results.Json(
                        new { message = string.IsNullOrWhiteSpace(result.FailureMessage)
                            ? "Sign-in failed." : result.FailureMessage,
                            code = result.FailureCode },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                if (result.RequiresSecondFactor)
                {
                    // First-pass only — MFA challenge issued. The Blazor client
                    // doesn't yet drive a second-factor screen, so we currently
                    // surface this as a clear error rather than partial success.
                    return Results.Json(
                        new { message = "Multi-factor authentication is required for this account; the business shell does not yet support the second-factor step." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                var summary = await BuildBusinessSessionSummaryAsync(
                    contextWorkflow,
                    result.UserId,
                    result.ActiveCompanyId,
                    cancellationToken);

                if (summary is null)
                {
                    return Results.Json(
                        new { message = "Sign-in succeeded but the user's company access could not be resolved." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                return Results.Ok(new BusinessSignInResponse
                {
                    Succeeded = true,
                    SessionToken = result.SessionToken,
                    Session = summary,
                    Message = string.Empty,
                    IsBootstrap = false
                });
            })
            .RequireRateLimiting("auth-login");

        app.MapGet(
            "/auth/session",
            async (
                HttpContext httpContext,
                Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
                global::Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
                CancellationToken cancellationToken) =>
            {
                var token = ReadBusinessSessionToken(httpContext);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Results.Unauthorized();
                }

                var result = await repository.ValidateSessionAsync(token, cancellationToken);
                if (!result.Succeeded)
                {
                    return Results.Unauthorized();
                }

                var summary = await BuildBusinessSessionSummaryAsync(
                    contextWorkflow,
                    result.UserId,
                    result.ActiveCompanyId,
                    cancellationToken);
                return summary is null ? Results.Unauthorized() : Results.Ok(summary);
            });

        app.MapPost(
            "/auth/logout",
            async (
                HttpContext httpContext,
                Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
                CancellationToken cancellationToken) =>
            {
                var token = ReadBusinessSessionToken(httpContext);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    await repository.RevokeSessionAsync(token, cancellationToken);
                }

                return Results.NoContent();
            });

        // M17 (AUDIT_2026-05-20 P2-4): switch the session's active company.
        // Updates business_sessions.active_company_id on the server (so the
        // M17 bind check in BusinessRouteGuard sees the new company) and
        // returns the refreshed session summary. The Blazor client uses the
        // returned summary to update BusinessShellState BEFORE flipping the
        // X-Active-Company-Id header on subsequent requests — order matters
        // because the bind check rejects any mismatch between header and
        // DB-stored active_company_id.
        app.MapPost(
            "/auth/switch-active-company",
            async (
                BusinessSwitchActiveCompanyRequest request,
                HttpContext httpContext,
                Citus.Platform.Core.Abstractions.IPlatformBusinessSessionRepository repository,
                global::Modules.CompanyAccess.SessionContext.ICompanySessionContextWorkflow contextWorkflow,
                CancellationToken cancellationToken) =>
            {
                var token = ReadBusinessSessionToken(httpContext);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Results.Unauthorized();
                }

                if (!CompanyId.TryParse(request.ActiveCompanyId, out var requestedCompanyId))
                {
                    return Results.Json(
                        new { message = "Active company id is missing or malformed." },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = await repository.SwitchActiveCompanyAsync(token, requestedCompanyId, cancellationToken);
                if (!result.Succeeded)
                {
                    // Distinguish "session not bound to this company at all"
                    // (403) from "session invalid / expired" (401). The repo
                    // surfaces both as FailureCode strings.
                    var status = result.FailureCode switch
                    {
                        "company_not_available" => StatusCodes.Status403Forbidden,
                        _ => StatusCodes.Status401Unauthorized
                    };
                    return Results.Json(
                        new { message = string.IsNullOrWhiteSpace(result.FailureMessage)
                            ? "Active-company switch failed." : result.FailureMessage,
                            code = result.FailureCode },
                        statusCode: status);
                }

                var summary = await BuildBusinessSessionSummaryAsync(
                    contextWorkflow,
                    result.UserId,
                    result.ActiveCompanyId,
                    cancellationToken);
                if (summary is null)
                {
                    return Results.Json(
                        new { message = "Active company switched but the refreshed company access could not be resolved." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                return Results.Ok(summary);
            });

        // ---------------------------------------------------------------------------
        // Self-serve password reset (Business shell only). Pair of public endpoints:
        // /auth/forgot-password issues a token + sends an email; /auth/reset-password
        // redeems the token and sets a new password. Both deliberately return the
        // same shape regardless of whether the email matches a real account, so an
        // attacker can't enumerate registered users by timing or response shape.
        // ---------------------------------------------------------------------------
        app.MapPost(
            "/auth/forgot-password",
            async (
                BusinessForgotPasswordRequest request,
                HttpContext httpContext,
                IConfiguration configuration,
                Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService resetService,
                Citus.Platform.Core.Abstractions.IPlatformVerificationNotificationSender notifier,
                CancellationToken cancellationToken) =>
            {
                // Always respond with this generic ack — never leak whether
                // the email is a known account.
                var ack = new
                {
                    ok = true,
                    message =
                        "If an account matches that email, a reset link has been sent. " +
                        "Check your inbox (the link expires in 15 minutes). " +
                        "If nothing arrives, your administrator may need to verify the SMTP configuration.",
                };

                if (string.IsNullOrWhiteSpace(request?.Email))
                {
                    return Results.Ok(ack);
                }

                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
                var issued = await resetService.IssueTokenAsync(request.Email, remoteIp, cancellationToken);
                if (issued is null)
                {
                    return Results.Ok(ack);
                }

                var publicBaseUrl = (configuration["AppHost:PublicBaseUrl"] ?? string.Empty).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(publicBaseUrl))
                {
                    // Fall back to the request's own origin if the AppHost
                    // public URL hasn't been configured. Better to emit a
                    // self-relative link than to no-op the email.
                    publicBaseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                }
                var resetUrl = $"{publicBaseUrl}/reset-password?token={Uri.EscapeDataString(issued.PlaintextToken)}";

                // SMTP-not-configured failure is treated as a soft failure
                // here — log the dispatch attempt but still return the ack
                // so the operator-facing message stays generic. Per agreement
                // the ack already hints at "ask admin to check SMTP" if no
                // mail arrives.
                await notifier.SendPasswordResetLinkAsync(
                    new Citus.Platform.Core.Runtime.PasswordResetLinkNotificationMessage
                    {
                        DispatchId = Guid.NewGuid(),
                        Destination = issued.Email,
                        RecipientDisplayName = issued.DisplayName,
                        ResetUrl = resetUrl,
                        ExpiresAtUtc = issued.ExpiresAtUtc,
                    },
                    cancellationToken);

                return Results.Ok(ack);
            });

        app.MapPost(
            "/auth/reset-password",
            async (
                BusinessResetPasswordRequest request,
                Citus.Platform.Core.Abstractions.IPlatformBusinessPasswordResetService resetService,
                CancellationToken cancellationToken) =>
            {
                if (request is null || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
                {
                    return Results.BadRequest(new
                    {
                        ok = false,
                        code = "missing_fields",
                        message = "Reset token and new password are required.",
                    });
                }

                var outcome = await resetService.RedeemTokenAsync(
                    request.Token, request.NewPassword, cancellationToken);

                if (!outcome.Succeeded)
                {
                    return Results.BadRequest(new
                    {
                        ok = false,
                        code = outcome.FailureCode,
                        message = outcome.FailureMessage,
                    });
                }

                return Results.Ok(new
                {
                    ok = true,
                    message = "Password updated. You can sign in now.",
                });
            });
    }
}
